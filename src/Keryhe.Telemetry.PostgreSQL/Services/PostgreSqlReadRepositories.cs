using System.Data.Common;
using Dapper;
using Npgsql;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;

namespace Keryhe.Telemetry.PostgreSQL.Services;

// =============================================================================
// PostgreSQL (plain) read repositories — the reference Dapper implementations.
// Connection comes from the host-configured NpgsqlDataSource (read connection string).
// =============================================================================

public class PostgreSqlTraceReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : TraceReadRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);

    // Npgsql binds a list parameter as a native array, so use = ANY(@ids) rather than
    // the base's IN @ids (which Dapper only expands for non-array providers like SqlServer).
    protected override string SpanIdInPredicate => "s.span_id = ANY(@ids)";
}

public class PostgreSqlMetricReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : MetricReadRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

public class PostgreSqlLogReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : LogReadRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

public class PostgreSqlResourceReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : ResourceReadRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

public class PostgreSqlTenantCatalogRepository(NpgsqlDataSource dataSource)
    : TenantCatalogRepositoryBase
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

public class PostgreSqlAlertRuleRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : AlertRuleRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);

    protected override string JsonParam(string parameterName) => $"CAST(@{parameterName} AS jsonb)";

    protected override string ReturningIdentity => "RETURNING id";

    protected override string ClaimFireSql => """
        UPDATE alert_rules
        SET last_fired_at = NOW()
        WHERE id = @ruleId
          AND tenant_id = @tenantId
          AND enabled = TRUE
          AND (last_fired_at IS NULL
               OR last_fired_at < NOW() - (@cooldownMinutes * INTERVAL '1 minute'))
        """;
}

/// <summary>
/// PostgreSQL (plain) implementation of the <see cref="IRetentionSettingsRepository"/> sweeps.
/// Span events and links are removed by the schema's <c>ON DELETE CASCADE</c> foreign keys, so
/// the trace sweep targets only <c>spans</c>.
///
/// The metric sweep is the exception to that pattern: it targets the data-point tables directly
/// on <c>time_unix_nano</c> rather than cascading from <c>metrics</c>, because after the 2.7.0
/// dedup a catalog row's <c>created_at</c> is "first seen" and cascading from it would discard a
/// metric's entire history.
///
/// The trace sweep is batched; the metric and log sweeps are not, and that asymmetry is deliberate.
/// <c>spans</c> is the largest table in the schema and the only one nothing else prunes, so its first
/// sweep is the biggest delete this system will ever run -- and on Postgres one enormous DELETE holds
/// a transaction open long enough to block autovacuum database-wide, emits all its WAL at once, and
/// loses every bit of progress if it is interrupted. The other two are left as single statements
/// because no index suits a bounded batch: the metric data-point tables are covered by BRIN and a
/// composite (metric_id, time_unix_nano), both of which serve one bulk delete well and a LIMIT badly.
/// Batching those would mean adding indexes, which is a schema change.
/// </summary>
public class PostgreSqlRetentionSettingsRepository(NpgsqlDataSource dataSource)
    : RetentionSettingsRepositoryBase
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);

    /// <summary>
    /// Rows removed per statement by the trace sweep. Bounded so a large first sweep does not run as
    /// one long transaction -- on Postgres that would block autovacuum from reclaiming dead tuples
    /// across the whole database, not just this table.
    /// </summary>
    private const int DeleteBatchSize = 50_000;

    /// <summary>
    /// Postgres has neither <c>DELETE TOP (n)</c> nor <c>DELETE ... LIMIT n</c>, so the batch is
    /// bounded by selecting a capped set of keys first. Keyed on <c>id</c> (the identity primary
    /// key) with an <c>ORDER BY start_time_unix_nano</c> that costs nothing extra: <c>idx_duration</c>'s
    /// leading column already serves both the <c>WHERE</c> and the <c>ORDER BY</c> as one forward
    /// index scan, so the sweep removes oldest-first without a separate sort.
    ///
    /// The <c>DELETE</c> repeats the cutoff predicate even though every row the CTE selected
    /// already satisfies it. That is redundant on plain Postgres but load-bearing on Timescale,
    /// which subclasses this repository unchanged: <c>spans</c> became a hypertable in schema
    /// 2.11.0, and a <c>DELETE ... WHERE id IN (...)</c> with no predicate on the partition column
    /// has to visit every chunk. Naming <c>start_time_unix_nano</c> lets chunk exclusion confine
    /// the delete to the oldest chunks, which is where all of its rows are anyway.
    /// </summary>
    private static readonly string TraceSweepSql = $"""
        WITH doomed AS (
            SELECT id FROM spans
            WHERE start_time_unix_nano < @cutoff
            ORDER BY start_time_unix_nano
            LIMIT {DeleteBatchSize}
        )
        DELETE FROM spans
        WHERE start_time_unix_nano < @cutoff AND id IN (SELECT id FROM doomed)
        """;

    public override async Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoffNano = CutoffNano(retentionPeriod);

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        // Each statement commits on its own -- that is the whole point, so do NOT wrap this loop in a
        // transaction. Doing so would reproduce exactly the long-running transaction the batching is
        // here to avoid. The loop terminates because the cutoff is computed once, so rows arriving
        // during the sweep are never older than it.
        var total = 0;
        int batch;
        do
        {
            batch = await conn.ExecuteAsync(new CommandDefinition(
                TraceSweepSql, new { cutoff = cutoffNano }, cancellationToken: cancellationToken));
            total += batch;
        } while (batch == DeleteBatchSize);

        return total;
    }

    public override async Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoffNano = CutoffNano(retentionPeriod);

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        var total = 0;
        foreach (var table in TelemetryIngestionHelpers.TimePrunedMetricTables)
        {
            total += await conn.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table} WHERE time_unix_nano < @cutoff",
                new { cutoff = cutoffNano }, cancellationToken: cancellationToken));
        }

        return total;
    }

    public override async Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoffNano = CutoffNano(retentionPeriod);

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM log_records WHERE time_unix_nano < @cutoff",
            new { cutoff = cutoffNano }, cancellationToken: cancellationToken));
    }
}
