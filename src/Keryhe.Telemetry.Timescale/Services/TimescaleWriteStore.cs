using Dapper;
using Npgsql;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data;

namespace Keryhe.Telemetry.Timescale.Services;

/// <summary>
/// TimescaleDB implementation of <see cref="ITelemetryWriteStore"/> — the write-path
/// retention sweeps. Span events and links are removed by the schema's <c>ON DELETE CASCADE</c>
/// foreign keys, so the trace sweep targets only <c>spans</c>.
///
/// The metric sweep is the exception to that pattern: it targets the data-point tables directly
/// on <c>time_unix_nano</c> rather than cascading from <c>metrics</c>, because after the 2.7.0
/// dedup a catalog row's <c>created_at</c> is "first seen" and cascading from it would discard a
/// metric's entire history. See <see cref="ITelemetryWriteStore"/>.
///
/// The trace sweep is batched; the metric and log sweeps are not, and that asymmetry is deliberate.
/// <c>spans</c> is the largest table in the schema and the only one nothing else prunes, so its first
/// sweep is the biggest delete this system will ever run -- and on Postgres one enormous DELETE holds
/// a transaction open long enough to block autovacuum database-wide, emits all its WAL at once, and
/// loses every bit of progress if it is interrupted. The other two are left as single statements
/// because no index suits a bounded batch: the metric data-point tables are covered by BRIN and a
/// composite (metric_id, time_unix_nano), both of which serve one bulk delete well and a LIMIT badly.
/// Batching those would mean adding indexes, which is a schema change.
///
/// Under Timescale these are on-demand supplements, not the primary mechanism: the schema already
/// carries <c>add_retention_policy</c> on the metric data-point tables (180 days) and
/// <c>log_records</c> (90 days). <c>spans</c> has neither a policy nor hypertable status, so
/// <see cref="DeleteOldTracesAsync"/> is the only trace retention that exists.
/// </summary>
public sealed class TimescaleWriteStore(NpgsqlDataSource dataSource) : ITelemetryWriteStore
{
    /// <summary>
    /// Rows removed per statement by the trace sweep. Bounded so a large first sweep does not run as
    /// one long transaction -- on Postgres that would block autovacuum from reclaiming dead tuples
    /// across the whole database, not just this table.
    /// </summary>
    private const int DeleteBatchSize = 50_000;

    /// <summary>
    /// Postgres has neither <c>DELETE TOP (n)</c> nor <c>DELETE ... LIMIT n</c>, so the batch is bounded
    /// by selecting a capped set of keys first.
    ///
    /// Keyed on <c>id</c> (the table's identity primary key) rather than <c>ctid</c>: the PK index is
    /// already there and this reads as ordinary SQL. <c>ctid</c> is the idiom for a table with no
    /// primary key, which is not the case here.
    ///
    /// The <c>ORDER BY</c> makes the sweep remove oldest-first, so an interrupted run leaves a clean
    /// prefix rather than holes scattered through the table. It costs nothing: <c>idx_start_time</c> is
    /// DESC and Postgres reads it backwards without a sort. The <c>LIMIT</c> inside the CTE is what lets
    /// the planner stop walking that index early instead of locating every matching row.
    ///
    /// Not a <c>const</c>: C# constant interpolated strings require every hole to be a constant string,
    /// and <see cref="DeleteBatchSize"/> is an int.
    /// </summary>
    private static readonly string TraceSweepSql = $"""
        WITH doomed AS (
            SELECT id FROM spans
            WHERE start_time_unix_nano < @cutoff
            ORDER BY start_time_unix_nano
            LIMIT {DeleteBatchSize}
        )
        DELETE FROM spans WHERE id IN (SELECT id FROM doomed)
        """;

    public async Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoffNano = CutoffNano(retentionPeriod);

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        // Each statement commits on its own -- that is the whole point, so do NOT wrap this loop in a
        // transaction. Doing so would reproduce exactly the long-running transaction the batching is
        // here to avoid. The loop terminates because the cutoff is computed once, so rows arriving
        // during the sweep are never older than it.
        //
        // The returned count is span rows. span_events and span_links come out via ON DELETE CASCADE,
        // so the real work per batch is larger than DeleteBatchSize suggests.
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

    public async Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
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

    public async Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoffNano = CutoffNano(retentionPeriod);

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM log_records WHERE time_unix_nano < @cutoff",
            new { cutoff = cutoffNano }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The retention cutoff as unix nanoseconds.
    ///
    /// The negative guard is load-bearing, not defensive noise: a negative period puts the cutoff
    /// in the FUTURE, at which point every predicate here matches every row in the table. On a
    /// retention API that is the difference between a no-op and erasing the telemetry store, so it
    /// fails loudly rather than quietly succeeding.
    /// </summary>
    private static long CutoffNano(TimeSpan retentionPeriod)
    {
        if (retentionPeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retentionPeriod),
                "Retention period cannot be negative; that would delete all telemetry.");

        return TimeConversion.DateTimeToUnixNano(DateTime.UtcNow - retentionPeriod);
    }
}
