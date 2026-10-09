namespace Keryhe.Telemetry.Core.Models;

/// <summary>
/// Read request for the summary rollups (plans/summary-rollups.md): a whole-minute window
/// <c>[StartNano, EndNano)</c> grouped into chart buckets of <see cref="BucketSeconds"/>, aligned to UTC
/// multiples of that width.
/// </summary>
public sealed class RollupQuery
{
    public long StartNano { get; init; }
    public long EndNano { get; init; }
    public long BucketSeconds { get; init; } = 60;
    public string? Service { get; init; }

    /// <summary>Logs only: keep severity numbers at or above this (a record with no severity is left out, as <c>&gt;=</c> leaves NULL out).</summary>
    public int? MinSeverity { get; init; }
}

/// <summary>One (chart bucket, service) row of the request rollup, partial rows already summed.</summary>
public sealed class RequestRollupAggregate
{
    public long BucketStartNano { get; init; }
    public string Service { get; init; } = "";
    public long RequestCount { get; init; }
    public long ErrorCount { get; init; }
    public long SumDurationNanos { get; init; }
    public long MaxDurationNanos { get; init; }
    public long[] Bands { get; init; } = new long[Data.DurationBands.Count];
}

/// <summary>One (chart bucket, severity number) row of the log rollup; severity -1 is "none".</summary>
public sealed class LogRollupAggregate
{
    public long BucketStartNano { get; init; }
    public int SeverityNumber { get; init; }
    public long RecordCount { get; init; }
}

/// <summary>Rows plus whether the query ran out of <c>SummaryTimeoutSeconds</c> (then <see cref="Rows"/> is empty).</summary>
public sealed class RollupReadResult<T>
{
    public IReadOnlyList<T> Rows { get; init; } = [];
    public bool TimedOut { get; init; }
}

/// <summary><c>GET /api/tenants/{id}/traces/summary</c>'s response (plans/summary-rollups.md, API section).</summary>
public sealed class RequestSummaryResult
{
    /// <summary>The chart bucket width the server chose from its fixed ladder.</summary>
    public int BucketSeconds { get; init; }

    /// <summary>Buckets from this minute on are left out: the rollup is not yet written for them (decision d).</summary>
    public DateTime WrittenThrough { get; init; }
    public RequestWindowSummary Summary { get; init; } = new();
    public List<RequestVolumeBucket> Buckets { get; init; } = [];
    public List<ServiceStats> Services { get; init; } = [];
    public List<RequestLatencyCell> Latency { get; init; } = [];

    /// <summary>The rollup query ran out of time: everything above is empty and must not be shown as "no requests".</summary>
    public bool TimedOut { get; init; }
}

/// <summary>Window totals over inbound spans; percentiles are approximate (duration histogram).</summary>
public sealed class RequestWindowSummary
{
    public long Count { get; init; }
    public long ErrorCount { get; init; }
    public double AvgMs { get; init; }
    public double MaxMs { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
    public double RatePerSecond { get; init; }
}

/// <summary>One chart bucket. <see cref="CoveredSeconds"/> is what the bucket actually spans (an edge bucket can be partial).</summary>
public sealed class RequestVolumeBucket
{
    public DateTime Timestamp { get; init; }
    public double CoveredSeconds { get; init; }
    public long Count { get; init; }
    public long ErrorCount { get; init; }
    public double SumDurationMs { get; init; }
    public double MaxDurationMs { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
}

/// <summary>One cell of the latency chart: a chart bucket times a duration band. Empty cells are omitted.</summary>
public sealed class RequestLatencyCell
{
    public DateTime XStart { get; init; }
    public DateTime XEnd { get; init; }

    /// <summary>Duration band 0-23 (<see cref="Data.DurationBands"/>); 23 is open-ended, so its <see cref="YEndMs"/> is only a display bound.</summary>
    public int Band { get; init; }
    public double YStartMs { get; init; }
    public double YEndMs { get; init; }
    public long Count { get; init; }
}

/// <summary><c>GET /api/tenants/{id}/logs/summary</c>'s response (plans/summary-rollups.md, API section).</summary>
public sealed class LogRollupSummaryResult
{
    public int BucketSeconds { get; init; }
    public DateTime WrittenThrough { get; init; }
    public long Total { get; init; }
    public List<LogRollupBucket> Buckets { get; init; } = [];
    public bool TimedOut { get; init; }
}

/// <summary>One chart bucket of the log rollup, by severity group (<c>RollupSummaryBuilder.SeverityGroup</c>).</summary>
public sealed class LogRollupBucket
{
    public DateTime Timestamp { get; init; }
    public double CoveredSeconds { get; init; }
    public long Trace { get; init; }
    public long Debug { get; init; }
    public long Info { get; init; }
    public long Warn { get; init; }
    public long Error { get; init; }
    public long Fatal { get; init; }
}
