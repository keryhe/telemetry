namespace Keryhe.Telemetry.Core;

// =============================================================================
// RETENTION SWEEPER INTERFACE
// =============================================================================

/// <summary>
/// The sweeps that enforce the retention policy (<see cref="IRetentionSettingsRepository"/>) on the
/// telemetry database. Implemented by every telemetry provider (PostgreSQL, SQL Server, MySQL,
/// ClickHouse); the windows themselves live in the control plane.
///
/// Retention is the ONLY delete this platform offers besides rule CRUD. Telemetry is append-only
/// observational data, so there is deliberately no way to remove a single trace, span, metric or
/// log record -- a targeted delete on an audit record is a liability, not a feature. The three
/// sweeps below prune by age and nothing else.
///
/// The retention WINDOWS are global (one settings row, set by the operator), but spans and log
/// records carry their own <c>tenant_id</c>, so the relational sweeps delete tenant by tenant
/// through the <c>(tenant_id, time)</c> access path those tables lead with. The tenants come from
/// the telemetry side itself (<c>resources</c>), not from the control plane, so data whose tenant no
/// longer exists there still expires. ClickHouse (<c>DROP PARTITION</c>) drops whole days instead,
/// so on it retention granularity is the day, and the returned counts are rows in the dropped
/// partitions.
/// </summary>
public interface IRetentionSweeper
{
    /// <summary>
    /// Removes spans that started before <c>UtcNow - retentionPeriod</c>; a span's events and links
    /// are columns on its row, so they go with it.
    ///
    /// Returns the number of SPAN rows removed, not the number of distinct traces -- counting
    /// traces would cost a second scan of the largest table in the schema for a number retention
    /// has no use for.
    /// </summary>
    Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes metric data points recorded before <c>UtcNow - retentionPeriod</c>, across the five
    /// data-point tables, each filtered on its own <c>time_unix_nano</c>. A point's exemplars live
    /// in a column on the point itself (schema 2.9.0), so they go with it.
    ///
    /// Deliberately does NOT touch the <c>metrics</c> catalog row, and deliberately does not key off
    /// <c>metrics.created_at</c>. Since the 2.7.0 dedup that column means "first ever seen" and never
    /// moves, so filtering on it would prune nothing -- and any row it did match would cascade away
    /// that metric's entire history, including points written seconds ago. A metric that goes quiet
    /// keeps its catalog row and stays listable.
    ///
    /// Returns data-point rows removed.
    /// </summary>
    Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes log records written before <c>UtcNow - retentionPeriod</c>. Returns rows removed.
    /// </summary>
    Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
}
