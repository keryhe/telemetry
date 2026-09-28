namespace Keryhe.Telemetry.Core;

/// <summary>
/// Bulk <c>metric_last_seen.last_seen_unix_nano</c> maintenance. Driven by
/// <c>Keryhe.Telemetry.Core.Data.MetricTouchWorker</c> on a periodic interval, never per gRPC
/// request or per ingestion flush — see that worker and
/// <c>Keryhe.Telemetry.Core.Data.MetricTouchTracker</c> (list-pages-server-side plan, Phase 5,
/// decision 27).
///
/// Every implementation must keep the GREATER of the stored and incoming value per
/// <c>metric_id</c> (a batch can legitimately carry an older <c>time_unix_nano</c> than what is
/// already recorded — late-arriving data, or a metric touched again after quieting down — and
/// must never move the "last seen" value backwards).
///
/// ClickHouse's implementation is a deliberate no-op: its <c>metric_last_seen</c> table is an
/// <c>AggregatingMergeTree</c> fed by materialized views on the data-point tables themselves, so
/// there is nothing for a periodic worker to write there. <see cref="IMetricTouchStore"/> is not
/// registered against a ClickHouse <c>Add ClickHouseCollectorServices</c> call at all — the no-op
/// still needs an implementation only because <c>MetricTouchWorker</c> is registered
/// unconditionally on every provider, same reasoning as <c>ApiKeyTouchStore</c>.
/// </summary>
public interface IMetricTouchStore
{
    /// <summary>
    /// Upserts <paramref name="touches"/> (metric id → newest observed <c>time_unix_nano</c> since
    /// the last drain), keeping the greater value per id. Callers sort by metric id before
    /// calling and cap each call under 4,000 rows — see <c>MetricTouchWorker</c>'s own doc comment
    /// for why (deadlock-avoidance across concurrent collector instances, decision 27).
    /// </summary>
    Task TouchAsync(IReadOnlyCollection<KeyValuePair<long, long>> touches, CancellationToken cancellationToken);
}
