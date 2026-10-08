using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;

namespace Keryhe.Telemetry.SqlServer.Services;

// =============================================================================
// SqlServer read repositories — separate dialect implementations.
// Connection comes from ConnectionStrings:Api. The shared base classes hold the
// dialect-neutral read SQL + shaping; only the alert CRUD/cooldown SQL differs.
// =============================================================================

/// <summary>
/// Shared bodies for the <c>AttributeKeyParamValue</c>/<c>AttributePredicate</c> dialect hooks
/// (list-pages-server-side plan, Phase 1), duplicated as an override on every SqlServer read
/// repository class below (there is no mixin, matching the existing convention for
/// <c>ResourceServiceNameExpr</c>/<c>JsonHasKeyExpr</c> in this file) but sharing one
/// implementation so the escaping logic isn't copy-pasted three times.
/// </summary>
internal static class SqlServerJsonAttributeHooks
{
    public static object KeyParamValue(string key)
        => $"$.\"{key.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    public static string Predicate(string column, string keyParam, string valueParam, bool negated)
    {
        var expr = $"LOWER(JSON_VALUE({column}, {keyParam}))";
        var valueExpr = $"LOWER({valueParam})";
        return negated ? $"({expr} IS NULL OR {expr} <> {valueExpr})" : $"{expr} = {valueExpr}";
    }
}

/// <summary>
/// SQL Server read connections. The database runs <c>READ_COMMITTED_SNAPSHOT ON</c> (schema 3.0.0),
/// so a plain connection already reads row versions and neither blocks behind nor blocks ingestion;
/// there is no per-connection isolation plumbing. Reads still run at <c>DEADLOCK_PRIORITY LOW</c>, so
/// if a read does deadlock with an ingestion flush the read is the victim, and a distinct
/// <c>Application Name</c> keeps a pooled read connection from being handed to a writer sharing the
/// same base connection string.
/// </summary>
internal static class SqlServerReadConnection
{
    private const string ReadApplicationName = "Keryhe.Telemetry.Api.Read";

    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { ApplicationName = ReadApplicationName };
        var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("SET DEADLOCK_PRIORITY LOW;", cancellationToken: cancellationToken));
        return conn;
    }

    /// <summary>
    /// Retries an idempotent read once on error 1205 (deadlock victim), after a short jittered
    /// delay -- an export that has already started streaming is explicitly NOT a caller of this
    /// (the client gets a truncated download and re-requests).
    /// </summary>
    public static async Task<T> WithRetryOnDeadlockAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            await Task.Delay(Random.Shared.Next(50, 200));
            return await operation();
        }
    }
}

/// <summary>
/// Trace/span ids are <c>varchar(32)</c>/<c>varchar(16)</c> with a binary collation (schema 3.0.0).
/// Dapper binds a plain string as <c>nvarchar(4000)</c>, which would force SQL Server to
/// implicitly convert the COLUMN and scan instead of seek; a sized ANSI <c>DbString</c> matches the
/// column's type exactly, so every id lookup is an index seek.
/// </summary>
internal static class SqlServerIds
{
    public static object Param(string? value, int length)
        => new DbString { Value = value, IsAnsi = true, IsFixedLength = false, Length = length };
}

public class SqlServerTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : TraceReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override object IdParam(string? value, int length) => SqlServerIds.Param(value, length);

    // Read connections: DEADLOCK_PRIORITY LOW on a distinct Application Name — see SqlServerReadConnection's own doc comment.
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await SqlServerReadConnection.OpenAsync(_connectionString, cancellationToken);

    protected override Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
        => SqlServerReadConnection.WithRetryOnDeadlockAsync(operation);

    // Same dialect hooks as SqlServerLogReadRepository, needed here too now that the service/tag
    // filters run in SQL (list-page-scale plan, Phase 4) — see DapperReadRepository's own doc
    // comments for why each hook is shaped the way it is (JSON_VALUE for a known-scalar value,
    // OPENJSON for a value-type-agnostic key existence check).
    protected override string JsonHasKeyExpr(string jsonColumn, string keyParam)
        => $"EXISTS (SELECT 1 FROM OPENJSON(ISNULL({jsonColumn}, '{{}}')) WHERE [key] = {keyParam})";
    protected override string PagingClause => "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";

    protected override object AttributeKeyParamValue(string key) => SqlServerJsonAttributeHooks.KeyParamValue(key);
    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => SqlServerJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);

    // SqlServer dialect: LIKE is case-insensitive under the default collation, and LIKE wildcards
    // escape with square brackets -- see SqlServerLogReadRepository's identical overrides.
    protected override string LikeOperator => "LIKE";
    protected override string EscapeLike(string value)
        => value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    // Decision 3/Phase 3 pin helper: SqlServer's created_at default is evaluated at statement
    // execution (not transaction start like Postgres) -- see SqlServerLogReadRepository's
    // identical override.
    protected override string DatabaseClockNowExpr => "SYSDATETIME()";
}

public class SqlServerMetricReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : MetricReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    // Read connections: DEADLOCK_PRIORITY LOW on a distinct Application Name — see SqlServerReadConnection's own doc comment.
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await SqlServerReadConnection.OpenAsync(_connectionString, cancellationToken);

    protected override Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
        => SqlServerReadConnection.WithRetryOnDeadlockAsync(operation);

    // SqlServer dialect: paging uses OFFSET/FETCH, not LIMIT/OFFSET.
    protected override string PagingClause => "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";
    protected override string LiteralPagingClause(int limit, int offset) => $"OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";

    // Load-bearing for the metric label-filter fix (list-pages-server-side plan, Phase 1):
    // MetricReadRepositoryBase's data-point getters call AttributePredicate/AttributeKeyParamValue
    // polymorphically, so SqlServer needs its own override here too, not just on the trace/log repos.
    protected override object AttributeKeyParamValue(string key) => SqlServerJsonAttributeHooks.KeyParamValue(key);
    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => SqlServerJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);

    // Load-bearing for the metrics catalog's service/name filters (list-pages-server-side plan,
    // Phase 5): SqlServerTraceReadRepository/SqlServerLogReadRepository already override these for
    // the same reason (see DapperReadRepository's own doc comments); MetricReadRepositoryBase's
    // catalog query calls them polymorphically too, so this class needs its own override.
    protected override string LikeOperator => "LIKE";
    protected override string EscapeLike(string value)
        => value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    // Standard tier (decision 26): newest-500, no cursor — not the analytics-tier keyset default.
    public override Task<Keryhe.Telemetry.Core.Models.MetricExemplarPage?> GetMetricExemplarsAsync(
        Keryhe.Telemetry.Core.Models.MetricExemplarQuery query, CancellationToken cancellationToken = default)
        => GetMetricExemplarsCappedAsync(query, cancellationToken);
}

public class SqlServerLogReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : LogReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override object IdParam(string? value, int length) => SqlServerIds.Param(value, length);

    // Read connections: DEADLOCK_PRIORITY LOW on a distinct Application Name — see SqlServerReadConnection's own doc comment.
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await SqlServerReadConnection.OpenAsync(_connectionString, cancellationToken);

    protected override Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
        => SqlServerReadConnection.WithRetryOnDeadlockAsync(operation);

    // SqlServer dialect: LIKE is case-insensitive under the default collation, JSON is read via
    // JSON_VALUE, paging uses OFFSET/FETCH, and LIKE wildcards escape with square brackets.
    protected override string LikeOperator => "LIKE";
    protected override string PagingClause => "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";
    protected override string EscapeLike(string value)
        => value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
    protected override object AttributeKeyParamValue(string key) => SqlServerJsonAttributeHooks.KeyParamValue(key);
    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => SqlServerJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);

    // Decision 3/Phase 2 pin helper: SqlServer's created_at default is evaluated at statement
    // execution (not transaction start like Postgres), so no 5-second back-off is
    // needed here.
    protected override string DatabaseClockNowExpr => "SYSDATETIME()";
}

public class SqlServerResourceReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : ResourceReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}

public class SqlServerTenantCatalogRepository(ControlPlaneConnection controlPlane)
    : TenantCatalogRepositoryBase
{
    private readonly string _connectionString = controlPlane.ConnectionString;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}

public class SqlServerAlertRuleRepository(ControlPlaneConnection controlPlane, ITenantContext tenantContext)
    : AlertRuleRepositoryBase(tenantContext)
{
    private readonly string _connectionString = controlPlane.ConnectionString;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    // SqlServer stores JSON in nvarchar(max); no cast required.
    protected override string JsonParam(string parameterName) => $"@{parameterName}";

    protected override string ReturningIdentity => "; SELECT CAST(SCOPE_IDENTITY() AS INT)";

    protected override string ClaimFireSql => """
        UPDATE alert_rules
        SET last_fired_at = GETUTCDATE()
        WHERE id = @ruleId
          AND tenant_id = @tenantId
          AND enabled = 1
          AND (last_fired_at IS NULL
               OR last_fired_at < DATEADD(MINUTE, -@cooldownMinutes, GETUTCDATE()))
        """;
}

/// <summary>
/// SQL Server implementation of <see cref="IRetentionSettingsRepository"/>: the shared settings read/write
/// from <see cref="RetentionSettingsRepositoryBase"/>, against the control-plane database.
/// </summary>
public class SqlServerRetentionSettingsRepository(ControlPlaneConnection controlPlane)
    : RetentionSettingsRepositoryBase
{
    private readonly string _connectionString = controlPlane.ConnectionString;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}

/// <summary>
/// SQL Server implementation of the <see cref="IRetentionSweeper"/> sweeps: the shared
/// batched, per-tenant shape from <see cref="RetentionSweeperBase"/>. The batch is
/// <c>DELETE TOP (n)</c>, which seeks the clustered key (<c>(tenant_id, time, id)</c> on spans and
/// log records) or the data-point time index, and is kept below SQL Server's lock-escalation
/// threshold (~5000 locks on one table/index in a single statement) so a sweep takes row locks only,
/// rather than escalating to an exclusive table lock that blocks -- and deadlocks with -- the
/// ingest path still appending to the same table.
/// </summary>
public class SqlServerRetentionSweeper(IConfiguration configuration)
    : RetentionSweeperBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    protected override int DeleteBatchSize => 4_000;

    protected override string BatchedDeleteSql(string table, string predicate)
        => $"DELETE TOP ({DeleteBatchSize}) FROM {table} WHERE {predicate}";

    // If a sweep does deadlock with an ingestion flush, make the sweep the victim: it simply
    // resumes on its next interval, whereas a flush victim burns one of its bounded retries.
    protected override async Task PrepareSweepConnectionAsync(DbConnection conn, CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition("SET DEADLOCK_PRIORITY LOW", cancellationToken: ct));
}

public class SqlServerRollupReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : RollupReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    // Read connections: DEADLOCK_PRIORITY LOW on a distinct Application Name, retried once on error 1205.
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await SqlServerReadConnection.OpenAsync(_connectionString, cancellationToken);

    protected override Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
        => SqlServerReadConnection.WithRetryOnDeadlockAsync(operation);
}
