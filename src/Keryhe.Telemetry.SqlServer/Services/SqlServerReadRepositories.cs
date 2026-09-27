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
/// SQL Server read isolation (list-pages-server-side plan, Phase 2, decision 35): every read
/// repository's connection opts into <c>SNAPSHOT</c> isolation at <c>DEADLOCK_PRIORITY LOW</c>
/// instead of read committed, so index-seek-plus-lookup reads never take the shared locks that
/// deadlock against ingestion's own writes. Factored here once rather than duplicated across the
/// trace/metric/log read repository classes, which each still own their own
/// <c>OpenConnectionAsync</c> override (matching this file's existing per-class-body convention)
/// but delegate its work to <see cref="OpenAsync"/>.
///
/// A distinct <c>Application Name</c> is appended to the connection string so a pooled read
/// connection is never handed back to a writer sharing the same base connection string (the
/// all-in-one host's single-container case) — independent of whether <c>sp_reset_connection</c>
/// would otherwise reset the isolation level on reuse.
/// </summary>
internal static class SqlServerReadConnection
{
    private const string ReadApplicationName = "Keryhe.Telemetry.Api.Read";

    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { ApplicationName = ReadApplicationName };
        var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "SET TRANSACTION ISOLATION LEVEL SNAPSHOT; SET DEADLOCK_PRIORITY LOW;",
            cancellationToken: cancellationToken));
        return conn;
    }

    /// <summary>
    /// Retries an idempotent read once on error 1205 (deadlock victim / snapshot update conflict),
    /// after a short jittered delay — an export that has already started streaming is explicitly
    /// NOT a caller of this (decision 35: "not retried; the client gets a truncated download and
    /// re-requests").
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

public class SqlServerTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext, TraceQueryCache traceQueryCache)
    : TraceReadRepositoryBase(tenantContext, traceQueryCache)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    // Decision 35: read repositories open under SNAPSHOT isolation, DEADLOCK_PRIORITY LOW, on a
    // distinct Application Name — see SqlServerReadConnection's own doc comment.
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await SqlServerReadConnection.OpenAsync(_connectionString, cancellationToken);

    protected override Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
        => SqlServerReadConnection.WithRetryOnDeadlockAsync(operation);

    // Same dialect hooks as SqlServerLogReadRepository, needed here too now that the service/tag
    // filters run in SQL (list-page-scale plan, Phase 4) — see DapperReadRepository's own doc
    // comments for why each hook is shaped the way it is (JSON_VALUE for a known-scalar value,
    // OPENJSON for a value-type-agnostic key existence check).
    protected override string ResourceServiceNameExpr(string resourceAlias = "r") => $"JSON_VALUE({resourceAlias}.attributes_json, '$.\"service.name\"')";
    protected override string JsonHasKeyExpr(string jsonColumn, string keyParam)
        => $"EXISTS (SELECT 1 FROM OPENJSON(ISNULL({jsonColumn}, '{{}}')) WHERE [key] = {keyParam})";
    protected override string PagingClause => "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";

    protected override object AttributeKeyParamValue(string key) => SqlServerJsonAttributeHooks.KeyParamValue(key);
    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => SqlServerJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);
}

public class SqlServerMetricReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : MetricReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    // Decision 35: read repositories open under SNAPSHOT isolation, DEADLOCK_PRIORITY LOW, on a
    // distinct Application Name — see SqlServerReadConnection's own doc comment.
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await SqlServerReadConnection.OpenAsync(_connectionString, cancellationToken);

    protected override Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
        => SqlServerReadConnection.WithRetryOnDeadlockAsync(operation);

    // SqlServer dialect: paging uses OFFSET/FETCH, not LIMIT/OFFSET.
    protected override string PagingClause => "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";

    // Load-bearing for the metric label-filter fix (list-pages-server-side plan, Phase 1):
    // MetricReadRepositoryBase's data-point getters call AttributePredicate/AttributeKeyParamValue
    // polymorphically, so SqlServer needs its own override here too, not just on the trace/log repos.
    protected override object AttributeKeyParamValue(string key) => SqlServerJsonAttributeHooks.KeyParamValue(key);
    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => SqlServerJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);
}

public class SqlServerLogReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : LogReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    // Decision 35: read repositories open under SNAPSHOT isolation, DEADLOCK_PRIORITY LOW, on a
    // distinct Application Name — see SqlServerReadConnection's own doc comment.
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await SqlServerReadConnection.OpenAsync(_connectionString, cancellationToken);

    protected override Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
        => SqlServerReadConnection.WithRetryOnDeadlockAsync(operation);

    // SqlServer dialect: LIKE is case-insensitive under the default collation, JSON is read via
    // JSON_VALUE, paging uses OFFSET/FETCH, and LIKE wildcards escape with square brackets.
    protected override string LikeOperator => "LIKE";
    protected override string ResourceServiceNameExpr(string resourceAlias = "r") => $"JSON_VALUE({resourceAlias}.attributes_json, '$.\"service.name\"')";
    protected override string PagingClause => "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";
    protected override string EscapeLike(string value)
        => value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
    protected override object AttributeKeyParamValue(string key) => SqlServerJsonAttributeHooks.KeyParamValue(key);
    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => SqlServerJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);

    // Decision 3/Phase 2 pin helper: SqlServer's created_at default is evaluated at statement
    // execution (not transaction start like Postgres/Timescale), so no 5-second back-off is
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

public class SqlServerTenantCatalogRepository(IConfiguration configuration)
    : TenantCatalogRepositoryBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}

/// <summary>
/// SQL Server implementation of <see cref="IRollupRepository"/>. The lease claim and state
/// reads/writes use the plain (read-committed) connection; only the recompute
/// (<see cref="LogRollupRepositoryBase.RollLogMinutesAsync"/>/<see cref="LogRollupRepositoryBase.RollLogHoursAsync"/>)
/// opens under SNAPSHOT isolation (decision 35), so it never takes shared locks against ingestion
/// while it reads raw <c>log_records</c> — this repository is the sole writer of the rollup tables
/// and holds the lease for the duration, so its own snapshot writes can't hit an update conflict.
/// </summary>
public class SqlServerLogRollupRepository(IConfiguration configuration) : LogRollupRepositoryBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    protected override async Task<DbConnection> OpenRecomputeConnectionAsync(CancellationToken cancellationToken)
        => await SqlServerReadConnection.OpenAsync(_connectionString, cancellationToken);
}

public class SqlServerAlertRuleRepository(IConfiguration configuration, ITenantContext tenantContext)
    : AlertRuleRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

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
/// SQL Server implementation of the <see cref="IRetentionSettingsRepository"/> sweeps. Span
/// events and links are removed by the schema's <c>ON DELETE CASCADE</c> foreign keys, so the
/// trace sweep targets only <c>spans</c>.
/// </summary>
public class SqlServerRetentionSettingsRepository(IConfiguration configuration)
    : RetentionSettingsRepositoryBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    /// <summary>
    /// Rows removed per statement by the retention sweeps. Kept below SQL Server's lock-escalation
    /// threshold (~5000 locks on one table/index in a single statement) so a sweep takes row locks
    /// only, rather than escalating to an exclusive table lock that blocks -- and deadlocks with --
    /// the ingest path still appending to the same table.
    /// </summary>
    private const int DeleteBatchSize = 4_000;

    public override Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => SweepAsync(["spans"], "start_time_unix_nano", retentionPeriod, cancellationToken);

    public override Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => SweepAsync(TelemetryIngestionHelpers.TimePrunedMetricTables, "time_unix_nano", retentionPeriod, cancellationToken);

    public override async Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var removed = await SweepAsync(["log_records"], "time_unix_nano", retentionPeriod, cancellationToken);
        await SweepAsync(["log_rollup_minute", "log_rollup_hour"], "bucket_unix_nano", retentionPeriod, cancellationToken);
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

        // If a sweep does deadlock with an ingestion flush, make the sweep the victim: it simply
        // resumes on its next interval, whereas a flush victim burns one of its bounded retries.
        await conn.ExecuteAsync(new CommandDefinition("SET DEADLOCK_PRIORITY LOW", cancellationToken: cancellationToken));

        var total = 0;
        foreach (var table in tables)
        {
            int batch;
            do
            {
                batch = await conn.ExecuteAsync(new CommandDefinition(
                    $"DELETE TOP ({DeleteBatchSize}) FROM {table} WHERE {timeColumn} < @cutoff",
                    new { cutoff = cutoffNano }, cancellationToken: cancellationToken));
                total += batch;
            } while (batch == DeleteBatchSize);
        }

        return total;
    }
}
