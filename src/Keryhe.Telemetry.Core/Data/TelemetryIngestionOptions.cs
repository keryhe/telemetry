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
}
