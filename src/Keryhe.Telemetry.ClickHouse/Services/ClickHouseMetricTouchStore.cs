using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// Deliberate no-op implementation of <see cref="IMetricTouchStore"/>. ClickHouse has no last-seen side table:
/// <see cref="ClickHouseBulkWriter"/> writes <c>metric_catalog</c> and <c>metric_series</c> (with their
/// <c>last_seen</c>) itself, so there is nothing for <c>MetricTouchWorker</c> to write here.
/// </summary>
public sealed class ClickHouseMetricTouchStore : IMetricTouchStore
{
    public Task TouchAsync(IReadOnlyCollection<KeyValuePair<long, long>> touches, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
