using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// RETENTION SETTINGS REPOSITORY INTERFACE
// =============================================================================

/// <summary>
/// Owns the DB-backed retention policy (<see cref="RetentionSettings"/>) and the sweeps that
/// enforce it. Replaces the former <c>ITelemetryWriteStore</c>: retention is a read-host
/// (<c>ConnectionStrings:Api</c>) concern now, alongside alert-rule CRUD
/// (<see cref="IAlertRuleRepository"/>), not a write-host one — <see cref="RetentionWorker"/>
/// (in <c>Keryhe.Telemetry.Api</c>) and the settings API both resolve this same scoped instance.
///
/// Retention is the ONLY delete this platform offers besides rule CRUD. Telemetry is append-only
/// observational data, so there is deliberately no way to remove a single trace, span, metric or
/// log record -- a targeted delete on an audit record is a liability, not a feature. The three
/// sweeps below prune by age and nothing else.
///
/// None of the sweeps is tenant-scoped, and that is deliberate rather than an oversight.
/// Retention is an operator concern; only <c>resources</c> carries a <c>tenant_id</c>, so
/// scoping would force a subquery on <c>resources</c> into every predicate and displace the
/// access paths these sweeps depend on -- the <c>time_unix_nano</c> indexes, Timescale's
/// <c>drop_chunks</c>, ClickHouse's partition drops. The settings row itself is likewise a
/// single global row, not per-tenant, for the same reason.
/// </summary>
public interface IRetentionSettingsRepository
{
    /// <summary>Reads the single, global retention settings row.</summary>
    Task<RetentionSettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates the single, global retention settings row. Always an <c>UPDATE</c>, never an
    /// <c>INSERT</c> — the row is seeded by the schema install, so two concurrent saves race to
    /// overwrite the same row rather than risk creating a second one.
    /// </summary>
    Task UpdateSettingsAsync(RetentionSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Removes spans that started before <c>UtcNow - retentionPeriod</c>. Span events and links
    /// go with them via <c>ON DELETE CASCADE</c> on the relational providers, and by explicit
    /// child deletes on ClickHouse.
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
