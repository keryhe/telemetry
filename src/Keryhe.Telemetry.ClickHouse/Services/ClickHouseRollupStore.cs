using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// Deliberate no-op <see cref="IRollupStore"/> (plans/summary-rollups.md). ClickHouse's
/// <c>request_rollup_minute</c> and <c>log_rollup_minute</c> are <c>AggregatingMergeTree</c> tables
/// fed by materialized views on <c>spans</c> and <c>log_records</c>, so there is no accumulator and
/// nothing for <c>RollupWorker</c> to write; <see cref="FedByViews"/> tells it to stay idle.
/// </summary>
public sealed class ClickHouseRollupStore : IRollupStore
{
    public bool FedByViews => true;

    public Task AppendRequestsAsync(IReadOnlyList<RequestRollupRow> rows, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task AppendLogsAsync(IReadOnlyList<LogRollupRow> rows, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
