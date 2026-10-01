using System.Data.Common;
using Dapper;
using MySqlConnector;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;

namespace Keryhe.Telemetry.MySql.Services;

// =============================================================================
// MySQL read repositories — separate dialect implementations.
// Connection comes from ConnectionStrings:Api. The shared base classes hold the
// dialect-neutral read SQL + shaping; only the log query and alert CRUD/cooldown
// SQL carry MySQL-specific overrides.
// =============================================================================

/// <summary>
/// Shared bodies for the <c>AttributeKeyParamValue</c>/<c>AttributePredicate</c> dialect hooks
/// (list-pages-server-side plan, Phase 1), duplicated as an override on every MySQL read
/// repository class below (there is no mixin) but sharing one implementation.
/// </summary>
internal static class MySqlJsonAttributeHooks
{
    public static object KeyParamValue(string key)
        => $"$.\"{key.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    public static string Predicate(string column, string keyParam, string valueParam, bool negated)
    {
        var expr = $"LOWER(JSON_UNQUOTE(JSON_EXTRACT({column}, {keyParam})))";
        var valueExpr = $"LOWER({valueParam})";
        return negated ? $"({expr} IS NULL OR {expr} <> {valueExpr})" : $"{expr} = {valueExpr}";
    }
}

public class MySqlTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : TraceReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    // Same dialect hooks as MySqlLogReadRepository, needed here too now that the service/tag
    // filters run in SQL (list-page-scale plan, Phase 4). JSON_KEYS + JSON_CONTAINS lists the
    // JSON object's top-level keys and tests membership — value-type-agnostic, unlike ->> which
    // returns NULL for an object/array value.
    protected override string JsonHasKeyExpr(string jsonColumn, string keyParam)
        => $"JSON_CONTAINS(JSON_KEYS(COALESCE({jsonColumn}, JSON_OBJECT())), JSON_QUOTE({keyParam}))";
    protected override object AttributeKeyParamValue(string key) => MySqlJsonAttributeHooks.KeyParamValue(key);
    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => MySqlJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);
    protected override string PagingClause => "LIMIT @limit OFFSET @offset";

    // MySQL dialect: LIKE is case-insensitive under the default _ci collation -- see
    // MySqlLogReadRepository's identical override.
    protected override string LikeOperator => "LIKE";

    // Decision 3/Phase 3 pin helper: MySQL's created_at default is evaluated at statement
    // execution, so no 5-second back-off is needed here -- see MySqlLogReadRepository's identical
    // override.
    protected override string DatabaseClockNowExpr => "CURRENT_TIMESTAMP(6)";

    // MySQL's `/` always yields a DECIMAL result even for integer operands; DIV keeps bucket-index
    // math as true integer floor division -- see MySqlLogReadRepository's identical override.
    protected override string BucketIndexExpr(string numerator, string denominator) => $"({numerator} DIV {denominator})";
}

public class MySqlMetricReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : MetricReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    // Load-bearing for the metric label-filter fix (list-pages-server-side plan, Phase 1):
    // MetricReadRepositoryBase's data-point getters call AttributePredicate/AttributeKeyParamValue
    // polymorphically, so MySQL needs its own override here too, not just on the trace/log repos.
    protected override object AttributeKeyParamValue(string key) => MySqlJsonAttributeHooks.KeyParamValue(key);
    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => MySqlJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);

    // MySQL's `/` always yields a DECIMAL result even for integer operands; DIV keeps Phase 4's
    // bucket-index math as true integer floor division — see MySqlLogReadRepository's identical
    // override. Same real bug shape as the ClickHouse BucketIndexExpr gap this phase also fixed.
    protected override string BucketIndexExpr(string numerator, string denominator) => $"({numerator} DIV {denominator})";

    // Load-bearing for the metrics catalog's service/name filters (list-pages-server-side plan,
    // Phase 5): MySqlTraceReadRepository/MySqlLogReadRepository already override these for the
    // same reason; MetricReadRepositoryBase's catalog query calls them polymorphically too.
    protected override string LikeOperator => "LIKE";

    // Standard tier (decision 26): newest-500, no cursor — not the analytics-tier keyset default.
    public override Task<Keryhe.Telemetry.Core.Models.MetricExemplarPage?> GetMetricExemplarsAsync(
        Keryhe.Telemetry.Core.Models.MetricExemplarQuery query, CancellationToken cancellationToken = default)
        => GetMetricExemplarsCappedAsync(query, cancellationToken);
}

public class MySqlLogReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : LogReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    // MySQL dialect: LIKE is case-insensitive under the default _ci collation, JSON is read via
    // the ->> operator (JSON_UNQUOTE(JSON_EXTRACT(...))), and paging uses LIMIT/OFFSET.
    // The attribute key "service.name" contains a dot, so the JSON path quotes it.
    protected override string LikeOperator => "LIKE";
    protected override string PagingClause => "LIMIT @limit OFFSET @offset";
    // MySQL LIKE uses backslash as the default escape character (matches the Postgres base default).

    // Decision 3/Phase 2 pin helper: MySQL's created_at default is evaluated at statement
    // execution (not transaction start like Postgres/Timescale), so no 5-second back-off is
    // needed here. Microsecond precision matches the column's DATETIME(6).
    protected override string DatabaseClockNowExpr => "CURRENT_TIMESTAMP(6)";

    // MySQL's `/` always yields a DECIMAL result even for integer operands; DIV keeps histogram
    // bucket-index math as true integer floor division.
    protected override string BucketIndexExpr(string numerator, string denominator) => $"({numerator} DIV {denominator})";
    protected override object AttributeKeyParamValue(string key) => MySqlJsonAttributeHooks.KeyParamValue(key);
    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => MySqlJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);
}

public class MySqlResourceReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : ResourceReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}

public class MySqlTenantCatalogRepository(IConfiguration configuration)
    : TenantCatalogRepositoryBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}

public class MySqlAlertRuleRepository(IConfiguration configuration, ITenantContext tenantContext)
    : AlertRuleRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    // MySQL stores JSON in native JSON columns; a string parameter is cast implicitly, no explicit cast.
    protected override string JsonParam(string parameterName) => $"@{parameterName}";

    protected override string ReturningIdentity => "; SELECT LAST_INSERT_ID()";

    protected override string ClaimFireSql => """
        UPDATE alert_rules
        SET last_fired_at = UTC_TIMESTAMP(6)
        WHERE id = @ruleId
          AND tenant_id = @tenantId
          AND enabled = 1
          AND (last_fired_at IS NULL
               OR last_fired_at < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @cooldownMinutes MINUTE))
        """;
}

/// <summary>
/// MySQL implementation of the <see cref="IRetentionSettingsRepository"/> sweeps: the shared
/// batched, per-tenant shape from <see cref="RetentionSettingsRepositoryBase"/>. The batch is
/// <c>DELETE ... LIMIT n</c>, which InnoDB serves from the clustered primary key
/// (<c>(tenant_id, time, id)</c> on spans and log records) or the data-point time index, bounded so
/// a sweep cannot escalate to a table lock or build an enormous undo log while ingestion appends.
/// </summary>
public class MySqlRetentionSettingsRepository(IConfiguration configuration)
    : RetentionSettingsRepositoryBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    protected override string BatchedDeleteSql(string table, string predicate)
        => $"DELETE FROM {table} WHERE {predicate} LIMIT {DeleteBatchSize}";
}
