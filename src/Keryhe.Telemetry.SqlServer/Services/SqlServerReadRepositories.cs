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

public class SqlServerTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext, TraceQueryCache traceQueryCache)
    : TraceReadRepositoryBase(tenantContext, traceQueryCache)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    // Same dialect hooks as SqlServerLogReadRepository, needed here too now that the service/tag
    // filters run in SQL (list-page-scale plan, Phase 4) — see DapperReadRepository's own doc
    // comments for why each hook is shaped the way it is (JSON_VALUE for a known-scalar value,
    // OPENJSON for a value-type-agnostic key existence check).
    protected override string ResourceServiceNameExpr(string resourceAlias = "r") => $"JSON_VALUE({resourceAlias}.attributes_json, '$.\"service.name\"')";
    protected override string JsonHasKeyExpr(string jsonColumn, string keyParam)
        => $"EXISTS (SELECT 1 FROM OPENJSON(ISNULL({jsonColumn}, '{{}}')) WHERE [key] = {keyParam})";
    protected override string PagingClause => "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";
}

public class SqlServerMetricReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : MetricReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    // SqlServer dialect: paging uses OFFSET/FETCH, not LIMIT/OFFSET.
    protected override string PagingClause => "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";
}

public class SqlServerLogReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : LogReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    // SqlServer dialect: LIKE is case-insensitive under the default collation, JSON is read via
    // JSON_VALUE, paging uses OFFSET/FETCH, and LIKE wildcards escape with square brackets.
    protected override string LikeOperator => "LIKE";
    protected override string ResourceServiceNameExpr(string resourceAlias = "r") => $"JSON_VALUE({resourceAlias}.attributes_json, '$.\"service.name\"')";
    protected override string PagingClause => "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";
    protected override string EscapeLike(string value)
        => value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
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

    public override Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => SweepAsync(["log_records"], "time_unix_nano", retentionPeriod, cancellationToken);

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
