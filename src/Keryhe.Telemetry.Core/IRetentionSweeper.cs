namespace Keryhe.Telemetry.Core;

// =============================================================================
// RETENTION SWEEPER INTERFACE
// =============================================================================

/// <summary>
/// The sweeps that enforce the retention policy (<see cref="IRetentionSettingsRepository"/>) on the
/// telemetry database. Implemented by every telemetry provider (PostgreSQL, SQL Server, MySQL,
/// ClickHouse, Parquet); the windows themselves live in the control plane, one set per tenant.
///
/// Retention is the ONLY delete this platform offers besides rule CRUD. Telemetry is append-only
/// observational data, so there is deliberately no way to remove a single trace, span, metric or
/// log record -- a targeted delete on an audit record is a liability, not a feature. The three
/// sweeps below prune by age and nothing else.
///
/// The windows are per tenant (<see cref="RetentionPolicy"/>), and every sweep deletes tenant by
/// tenant. The tenants swept come from the telemetry side itself (<c>resources</c>, the Parquet
/// folders, every ClickHouse row), not from the control plane, so data whose tenant no longer exists
/// there still expires, on <see cref="RetentionDefaults"/>. The relational providers delete rows
/// through the <c>(tenant_id, time)</c> access path (data points through <c>metric_id</c>). ClickHouse
/// drops whole days past the longest window in effect and lightweight-deletes the days between a
/// shorter window's cutoff and that boundary; Parquet deletes <c>(tenant, day)</c> folders. On both,
/// retention granularity is the day, and the returned counts are the rows removed.
/// </summary>
public interface IRetentionSweeper
{
    /// <summary>
    /// Removes spans that started before each tenant's trace cutoff; a span's events and links
    /// are columns on its row, so they go with it.
    ///
    /// Returns the number of SPAN rows removed, not the number of distinct traces -- counting
    /// traces would cost a second scan of the largest table in the schema for a number retention
    /// has no use for.
    /// </summary>
    Task<int> DeleteOldTracesAsync(RetentionPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes metric data points recorded before each tenant's metric cutoff, across the five
    /// data-point tables, each filtered on its own <c>time_unix_nano</c> (the relational tables carry
    /// no tenant column: a tenant's points are found through its <c>metrics</c> rows). A point's
    /// exemplars live in a column on the point itself (schema 2.9.0), so they go with it.
    ///
    /// Deliberately does NOT touch the <c>metrics</c> catalog row, and deliberately does not key off
    /// <c>metrics.created_at</c>. Since the 2.7.0 dedup that column means "first ever seen" and never
    /// moves, so filtering on it would prune nothing -- and any row it did match would cascade away
    /// that metric's entire history, including points written seconds ago. A metric that goes quiet
    /// keeps its catalog row and stays listable.
    ///
    /// Returns data-point rows removed.
    /// </summary>
    Task<int> DeleteOldMetricDataPointsAsync(RetentionPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes log records written before each tenant's log cutoff. Returns rows removed.
    /// </summary>
    Task<int> DeleteOldLogRecordsAsync(RetentionPolicy policy, CancellationToken cancellationToken = default);
}
