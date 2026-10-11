using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// Deliberate no-op <see cref="IRollupStore"/>: the Parquet writer writes the request and log rollups itself, with each
/// flush (phase 3), so there is no accumulator and nothing for <c>RollupWorker</c> to write. <see cref="FedByViews"/>
/// (true: "the provider writes the rollup") keeps the worker idle.
/// </summary>
public sealed class ParquetRollupStore : IRollupStore
{
    public bool FedByViews => true;

    public Task AppendRequestsAsync(IReadOnlyList<RequestRollupRow> rows, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AppendLogsAsync(IReadOnlyList<LogRollupRow> rows, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Deliberate no-op <see cref="IMetricTouchStore"/>: the writer maintains <c>metric_catalog</c> itself (phase 5).</summary>
public sealed class ParquetMetricTouchStore : IMetricTouchStore
{
    public Task TouchAsync(IReadOnlyCollection<KeyValuePair<long, long>> touches, CancellationToken cancellationToken) => Task.CompletedTask;
}
