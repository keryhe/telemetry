namespace Keryhe.Telemetry.Core;

/// <summary>One partial row of <c>request_rollup_minute</c> (inbound spans of one tenant/service/minute).</summary>
public sealed class RequestRollupRow
{
    public long TenantId { get; init; }
    public string ServiceName { get; init; } = "";
    public long BucketStartUnixNano { get; init; }
    public long RequestCount { get; set; }
    public long ErrorCount { get; set; }
    public long SumDurationNanos { get; set; }
    public long MaxDurationNanos { get; set; }
    /// <summary>Per-band counts, <see cref="Data.DurationBands.Count"/> entries (h00..h23).</summary>
    public long[] Bands { get; } = new long[Data.DurationBands.Count];
}

/// <summary>One partial row of <c>log_rollup_minute</c>. <see cref="SeverityNumber"/> is -1 when the record had none.</summary>
public sealed class LogRollupRow
{
    public long TenantId { get; init; }
    public string ServiceName { get; init; } = "";
    public int SeverityNumber { get; init; }
    public long BucketStartUnixNano { get; init; }
    public long RecordCount { get; set; }
}

/// <summary>
/// Appends partial rollup rows (plans/summary-rollups.md). Driven by <c>RollupWorker</c> once a
/// minute has closed; a plain append, never an upsert, so concurrent collectors never contend.
/// ClickHouse's implementation is a deliberate no-op (<see cref="FedByViews"/>): its rollup tables
/// are fed by materialized views on <c>spans</c>/<c>log_records</c>, like <see cref="IMetricTouchStore"/>.
/// </summary>
public interface IRollupStore
{
    /// <summary>True when the database maintains the rollup itself, so the accumulator is not fed at all.</summary>
    bool FedByViews { get; }

    /// <summary>Appends <paramref name="rows"/> (already sorted by tenant, minute, service; at most the batch size).</summary>
    Task AppendRequestsAsync(IReadOnlyList<RequestRollupRow> rows, CancellationToken cancellationToken);

    /// <summary>Appends <paramref name="rows"/> (already sorted by tenant, minute, service, severity).</summary>
    Task AppendLogsAsync(IReadOnlyList<LogRollupRow> rows, CancellationToken cancellationToken);
}
