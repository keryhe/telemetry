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

public class MySqlTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext, TraceQueryCache traceQueryCache)
    : TraceReadRepositoryBase(tenantContext, traceQueryCache)
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
    protected override string ResourceServiceNameExpr(string resourceAlias = "r") => $"{resourceAlias}.attributes_json ->> '$.\"service.name\"'";
    protected override string JsonHasKeyExpr(string jsonColumn, string keyParam)
        => $"JSON_CONTAINS(JSON_KEYS(COALESCE({jsonColumn}, JSON_OBJECT())), JSON_QUOTE({keyParam}))";
    protected override object AttributeKeyParamValue(string key) => MySqlJsonAttributeHooks.KeyParamValue(key);
    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => MySqlJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);
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
}

public class MySqlLogReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : LogReadRepositoryBase(tenantContext)
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
    protected override string ResourceServiceNameExpr(string resourceAlias = "r") => $"{resourceAlias}.attributes_json ->> '$.\"service.name\"'";
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

/// <summary>
/// MySQL implementation of <see cref="IRollupRepository"/>. The recompute's <c>INSERT ... SELECT</c>
/// runs at READ COMMITTED explicitly: under InnoDB's default REPEATABLE READ it would take shared
/// locks on the <c>log_records</c> rows it reads, blocking ingestion.
/// </summary>
public class MySqlLogRollupRepository(IConfiguration configuration) : LogRollupRepositoryBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    protected override string IntDivExpr(string numerator, string denominator) => $"({numerator} DIV {denominator})";

    protected override async Task<DbTransaction> BeginRecomputeTransactionAsync(DbConnection conn, CancellationToken cancellationToken)
        => await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
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
/// MySQL implementation of the <see cref="IRetentionSettingsRepository"/> sweeps. Span events
/// and links are removed by the schema's <c>ON DELETE CASCADE</c> foreign keys, so the trace
/// sweep targets only <c>spans</c>.
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

    /// <summary>
    /// Rows removed per statement by the retention sweeps. Bounded so a sweep cannot escalate to a
    /// table lock (or build an enormous InnoDB undo log) while the ingest path is still appending.
    /// </summary>
    private const int DeleteBatchSize = 50_000;

    public override Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => SweepAsync(["spans"], "start_time_unix_nano", retentionPeriod, cancellationToken);

    public override Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => SweepAsync(TelemetryIngestionHelpers.TimePrunedMetricTables, "time_unix_nano", retentionPeriod, cancellationToken);

    public override async Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var removed = await SweepAsync(["log_records"], "time_unix_nano", retentionPeriod, cancellationToken);
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await SweepLogRollupTablesAsync(conn, CutoffNano(retentionPeriod), cancellationToken);
        return removed;
    }

    /// <summary>
    /// Deletes every row in <paramref name="tables"/> whose <paramref name="timeColumn"/> predates the
    /// cutoff, in bounded chunks. Returns the total rows removed.
    ///
    /// The table and column names are interpolated rather than parameterized because they are not
    /// user input: they are compile-time literals and the entries of
    /// <see cref="TelemetryIngestionHelpers.TimePrunedMetricTables"/>.
    /// </summary>
    private async Task<int> SweepAsync(
        IReadOnlyList<string> tables,
        string timeColumn,
        TimeSpan retentionPeriod,
        CancellationToken cancellationToken)
    {
        var cutoffNano = CutoffNano(retentionPeriod);

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var total = 0;
        foreach (var table in tables)
        {
            int batch;
            do
            {
                batch = await conn.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM {table} WHERE {timeColumn} < @cutoff LIMIT {DeleteBatchSize}",
                    new { cutoff = cutoffNano }, cancellationToken: cancellationToken));
                total += batch;
            } while (batch == DeleteBatchSize);
        }

        return total;
    }
}
