namespace Keryhe.Telemetry.Core;

// =============================================================================
// TELEMETRY WRITE STORE INTERFACE
// =============================================================================

/// <summary>
/// Provider-specific retention operations for the write path. The thin write
/// repositories (<see cref="ITraceWriteRepository"/> etc.) enqueue stores to the
/// ingestion channel directly, but delegate their <c>Delete*</c> operations here so
/// the actual DML is owned by the active provider rather than EF Core.
///
/// Retention is the ONLY delete this platform offers. Telemetry is append-only
/// observational data, so there is deliberately no way to remove a single trace, span,
/// metric or log record -- a targeted delete on an audit record is a liability, not a
/// feature. Everything here prunes by age and nothing else.
///
/// None of these is tenant-scoped, and that is deliberate rather than an oversight.
/// Retention is an operator concern; only <c>resources</c> carries a <c>tenant_id</c>, so
/// scoping would force a subquery on <c>resources</c> into every predicate and displace the
/// access paths these sweeps depend on -- the <c>time_unix_nano</c> indexes, Timescale's
/// <c>drop_chunks</c>, ClickHouse's partition drops.
/// </summary>
public interface ITelemetryWriteStore
{
    /// <summary>
    /// Removes spans that started before <c>UtcNow - retentionPeriod</c>. Span events and links
    /// go with them via <c>ON DELETE CASCADE</c> on the relational providers, and by explicit
    /// child deletes on ClickHouse.
    ///
    /// Returns the number of SPAN rows removed, not the number of distinct traces -- counting
    /// traces would cost a second scan of the largest table in the schema for a number retention
    /// has no use for.
    ///
    /// This is the only trace retention that exists anywhere: unlike the metric and log tables,
    /// <c>spans</c> is not a Timescale hypertable and carries no <c>add_retention_policy</c>.
    /// </summary>
    Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes metric data points recorded before <c>UtcNow - retentionPeriod</c>, across
    /// <see cref="Keryhe.Telemetry.Core.ITelemetryWriteStore"/>'s six time-pruned tables -- the five
    /// data-point tables plus <c>exemplars</c>, each filtered on its own <c>time_unix_nano</c>.
    ///
    /// Deliberately does NOT touch the <c>metrics</c> catalog row, and deliberately does not key off
    /// <c>metrics.created_at</c>. Since the 2.7.0 dedup that column means "first ever seen" and never
    /// moves, so filtering on it would prune nothing -- and any row it did match would cascade away
    /// that metric's entire history, including points written seconds ago. A metric that goes quiet
    /// keeps its catalog row and stays listable.
    ///
    /// Returns data-point and exemplar rows removed.
    /// </summary>
    Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes log records written before <c>UtcNow - retentionPeriod</c>. Returns rows removed.
    /// </summary>
    Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
}
