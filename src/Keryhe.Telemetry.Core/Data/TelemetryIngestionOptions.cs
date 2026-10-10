namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Bounds for the ingestion write path. Bound from the <c>Telemetry:Ingestion</c> configuration
/// section.
///
/// Replaces two previously-hardcoded values that measured the wrong thing: the ingestion
/// channel's capacity (10,000 BATCHES, i.e. OTLP exports, not records -- a single export's size is
/// entirely client-controlled, so that ceiling bounded nothing meaningful) and the worker's single
/// <c>MaxBatchSize</c> constant (which counted <c>TraceModel</c> instances rather than the spans
/// that actually become rows and array parameters on flush).
///
/// Traces are governed by SPAN count, not trace count, throughout -- a trace batch capped by trace
/// count can still become an arbitrarily large SQL statement, since one trace can carry thousands
/// of spans.
/// </summary>
public sealed class TelemetryIngestionOptions
{
    /// <summary>Configuration section name these options bind from.</summary>
    public const string SectionName = "Telemetry:Ingestion";

    /// <summary>
    /// Maximum log records resident in the ingestion queue (written by a gRPC handler but not yet
    /// flushed) before a further write blocks. This -- not the channel itself, which is unbounded
    /// -- is what bounds worst-case ingestion memory, and it bounds it independently of how large
    /// any single OTLP export is.
    /// </summary>
    public int MaxQueuedLogRecords { get; set; } = 200_000;

    /// <summary>Maximum metrics resident in the ingestion queue. See <see cref="MaxQueuedLogRecords"/>.</summary>
    public int MaxQueuedMetrics { get; set; } = 200_000;

    /// <summary>
    /// Maximum SPANS (not traces) resident in the ingestion queue. See
    /// <see cref="MaxQueuedLogRecords"/>; spans, not <c>TraceModel</c> instances, are the unit
    /// that matters for both memory and eventual statement size.
    /// </summary>
    public int MaxQueuedSpans { get; set; } = 200_000;

    /// <summary>
    /// Bytes each signal's queue may hold, measured as the protobuf size of the requests that produced the queued records
    /// (the in-memory models are larger than their wire form, so this is a proxy; the default leaves room for that). A
    /// request is admitted when both its records and its bytes fit, or the queue is empty. 0 turns the byte budget off.
    /// </summary>
    public long MaxQueuedBytesPerSignal { get; set; } = 256L * 1024 * 1024;

    /// <summary>Maximum log records merged into one flush batch handed to the bulk writer.</summary>
    public int MaxLogFlushBatchSize { get; set; } = 2_000;

    /// <summary>Maximum metrics merged into one flush batch handed to the bulk writer.</summary>
    public int MaxMetricFlushBatchSize { get; set; } = 2_000;

    /// <summary>
    /// Maximum SPANS merged into one trace flush batch handed to the bulk writer. Kept distinct
    /// from <see cref="MaxLogFlushBatchSize"/> / <see cref="MaxMetricFlushBatchSize"/> so a trace
    /// batch cannot become a 100,000-row statement just because it happened to contain a small
    /// number of very large traces.
    /// </summary>
    public int MaxTraceFlushSpanBatchSize { get; set; } = 2_000;

    /// <summary>
    /// Number of concurrent flush loops run per signal (logs / traces / metrics each get their
    /// own set of this many). Each loop drains the shared channel and flushes independently, so
    /// DB write latency overlaps across loops instead of the whole signal serializing behind one
    /// connection. The in-memory drain step itself (picking the next batch off the channel) is
    /// still serialized per signal via an internal lock -- only the flush (the actual DB round
    /// trip, including retries) runs concurrently -- see <see cref="TelemetryIngestionWorker"/>.
    /// Defaults to 4, comfortably inside the default connection-pool size of every supported
    /// provider.
    /// </summary>
    public int FlushConcurrency { get; set; } = 4;

    /// <summary>
    /// How long a drain loop waits, once a signal has something queued, for a full batch to build up
    /// before flushing whatever is there, in milliseconds. 0 (the default) flushes as soon as anything
    /// is queued. A column store such as ClickHouse wants a few large inserts rather than many small
    /// ones; a linger trades up to this much extra commit latency for larger batches. The wait runs
    /// under the per-signal drain lock, so concurrent loops linger one after another rather than all
    /// waking together and splitting one interval's records into several small batches.
    /// </summary>
    public int FlushLingerMilliseconds { get; set; }

    /// <summary>
    /// Maximum retry attempts for a batch flush that throws, after the first attempt, before the
    /// batch is dropped. Defaults to 5 (6 attempts total).
    /// </summary>
    public int MaxFlushRetries { get; set; } = 5;

    /// <summary>
    /// Base delay before the first retry, in milliseconds. Doubles on each subsequent attempt
    /// (capped at <see cref="RetryMaxDelayMilliseconds"/>) -- standard exponential backoff, no
    /// jitter. Defaults to 200.
    /// </summary>
    public int RetryBaseDelayMilliseconds { get; set; } = 200;

    /// <summary>
    /// Ceiling on the exponential backoff delay between retries, in milliseconds. Defaults to
    /// 5,000 -- with the default <see cref="MaxFlushRetries"/> of 5, the cumulative wait across
    /// all retries is 200+400+800+1600+3200 = 6.2s, enough to ride out a brief DB blip or
    /// failover without losing the batch.
    /// </summary>
    public int RetryMaxDelayMilliseconds { get; set; } = 5_000;

    /// <summary>The wait applied when <see cref="MaxGateWaitMilliseconds"/> is not set, in milliseconds.</summary>
    public const int DefaultMaxGateWaitMilliseconds = 2_000;

    /// <summary>
    /// How long an export waits for room in a full ingestion queue before it is refused with a retryable status
    /// (gRPC <c>UNAVAILABLE</c> + <c>RetryInfo</c>), in milliseconds. 0 refuses at once when the queue is full; a
    /// negative value waits without limit (the pre-bounded behaviour: the export is held open until the client's own
    /// deadline). Unset means the provider's default: <see cref="DefaultMaxGateWaitMilliseconds"/>, or on ClickHouse the
    /// day-buffer linger plus that, because its worker frees room a whole buffer at a time.
    /// </summary>
    public int? MaxGateWaitMilliseconds { get; set; }

    /// <summary>
    /// The delay a refused client is told to wait (<c>RetryInfo</c> / <c>Retry-After</c>), in milliseconds, before
    /// jitter of up to 50% is added so a fleet of refused clients does not return at the same instant.
    /// </summary>
    public int RejectRetryDelayMilliseconds { get; set; } = 1_000;

    /// <summary>
    /// The most extra flush attempts spent isolating permanently bad records in one batch (see
    /// <see cref="BatchBisector"/>). What is left when the cap is reached is dropped.
    /// </summary>
    public int MaxSplitFlushes { get; set; } = 64;

    /// <summary>Readiness fails when the control-plane key lookup has been failing for longer than this, in seconds.</summary>
    public int ReadinessControlPlaneSeconds { get; set; } = 60;

    /// <summary>Readiness fails when a signal's queue has been continuously full for longer than this, in seconds.</summary>
    public int ReadinessSaturatedSeconds { get; set; } = 10;

    /// <summary>The wait to apply, or null for unbounded.</summary>
    public TimeSpan? EffectiveMaxGateWait => (MaxGateWaitMilliseconds ?? DefaultMaxGateWaitMilliseconds) switch
    {
        < 0 => null,
        var ms => TimeSpan.FromMilliseconds(ms)
    };

    public void Validate()
    {
        static void NonNegative(int value, string name)
        {
            if (value < 0) throw new InvalidOperationException($"{SectionName}:{name} must not be negative (was {value}).");
        }
        static void Positive(int value, string name)
        {
            if (value <= 0) throw new InvalidOperationException($"{SectionName}:{name} must be greater than 0 (was {value}).");
        }
        if (MaxQueuedBytesPerSignal < 0) throw new InvalidOperationException($"{SectionName}:{nameof(MaxQueuedBytesPerSignal)} must not be negative (was {MaxQueuedBytesPerSignal}).");
        NonNegative(RejectRetryDelayMilliseconds, nameof(RejectRetryDelayMilliseconds));
        NonNegative(MaxSplitFlushes, nameof(MaxSplitFlushes));
        Positive(ReadinessControlPlaneSeconds, nameof(ReadinessControlPlaneSeconds));
        Positive(ReadinessSaturatedSeconds, nameof(ReadinessSaturatedSeconds));
    }
}
