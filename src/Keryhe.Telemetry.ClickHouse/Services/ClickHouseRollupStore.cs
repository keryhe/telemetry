using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// Deliberate no-op <see cref="IRollupStore"/> (plans/summary-rollups.md). <c>request_rollup_minute</c> and
/// <c>log_rollup_minute</c> are written by <see cref="ClickHouseBulkWriter"/> itself, in the same flush as the raw
/// rows (plans/clickhouse-redesign phase 3; there are no materialized views), so there is no accumulator and nothing
/// for <c>RollupWorker</c> to write; <see cref="FedByViews"/> (kept <c>true</c> for that meaning: "the provider writes
/// the rollup") tells it to stay idle.
/// </summary>
public sealed class ClickHouseRollupStore : IRollupStore
{
    public bool FedByViews => true;

    public Task AppendRequestsAsync(IReadOnlyList<RequestRollupRow> rows, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task AppendLogsAsync(IReadOnlyList<LogRollupRow> rows, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
