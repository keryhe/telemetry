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

public class MySqlTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : TraceReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}

public class MySqlMetricReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : MetricReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
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
    protected override string ResourceServiceNameExpr => "r.attributes_json ->> '$.\"service.name\"'";
    protected override string PagingClause => "LIMIT @limit OFFSET @offset";
    // MySQL LIKE uses backslash as the default escape character (matches the Postgres base default).

    // MySQL's `/` always yields a DECIMAL result even for integer operands; DIV keeps histogram
    // bucket-index math as true integer floor division.
    protected override string BucketIndexExpr(string numerator, string denominator) => $"({numerator} DIV {denominator})";
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
