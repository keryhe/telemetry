using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// Deliberate no-op implementation of <see cref="IMetricTouchStore"/> (list-pages-server-side
/// plan, Phase 5, decision 27). ClickHouse's <c>metric_last_seen</c> table is an
/// <c>AggregatingMergeTree</c> fed by one materialized view per data-point table (see
/// <c>ClickHouse-Schema.sql</c>), so there is nothing for <c>MetricTouchWorker</c> to write here —
/// new rows in any data-point table already update it as a side effect of the insert itself, with
/// no periodic worker and no mutation.
/// </summary>
public sealed class ClickHouseMetricTouchStore : IMetricTouchStore
{
    public Task TouchAsync(IReadOnlyCollection<KeyValuePair<long, long>> touches, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
