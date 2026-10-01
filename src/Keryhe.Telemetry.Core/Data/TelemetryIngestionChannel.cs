using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Keryhe.Telemetry.Core.Data.Threading;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// The three ingestion channels, one per signal, plus the <see cref="RecordCountGate"/> that
/// bounds each.
///
/// The channels themselves are UNBOUNDED. Backpressure used to live on the channel (bounded to
/// 10,000 batches, <c>FullMode.Wait</c>), but a batch is an OTLP export, whose size is entirely
/// client-controlled -- "10,000 queued" could mean 10,000 records or 10 billion depending only on
/// what a caller sends in one export. The gates below bound the thing that actually matters:
/// resident records (log records, spans, metrics). Write repositories call
/// <c>gate.AcquireAsync(count, ct)</c> before writing to the paired channel, and
/// <see cref="TelemetryIngestionWorker"/> calls <c>gate.Release(count)</c> once it has drained and
/// attempted to flush a batch -- on success or failure, so a dropped batch does not also leak gate
/// capacity forever.
///
/// <c>SingleReader = false</c>: <see cref="TelemetryIngestionWorker"/> now runs
/// <see cref="TelemetryIngestionOptions.FlushConcurrency"/> concurrent drain loops per signal
/// against the SAME channel, so DB write latency overlaps instead of one flush blocking the next.
/// </summary>
public sealed class TelemetryIngestionChannel
{
    public Channel<List<LogRecordModel>> Logs { get; } =
        Channel.CreateUnbounded<List<LogRecordModel>>(new UnboundedChannelOptions { SingleReader = false });

    /// <summary>
    /// Carries flat spans, not <see cref="TraceModel"/> groupings -- <c>TraceWriteRepository</c>
    /// flattens each trace's spans (resolving each span's effective resource/scope fallback once,
    /// there) before writing, so the batch size here is already the span count the worker's
    /// <c>sizeOf</c> and <see cref="TraceGate"/> both need, with no further unwrapping downstream.
    /// </summary>
    public Channel<List<SpanModel>> Traces { get; } =
        Channel.CreateUnbounded<List<SpanModel>>(new UnboundedChannelOptions { SingleReader = false });

    public Channel<List<MetricModel>> Metrics { get; } =
        Channel.CreateUnbounded<List<MetricModel>>(new UnboundedChannelOptions { SingleReader = false });

    /// <summary>Gate on resident LOG RECORDS. See <see cref="TelemetryIngestionOptions.MaxQueuedLogRecords"/>.</summary>
    public RecordCountGate LogGate { get; }

    /// <summary>Gate on resident SPANS, not traces. See <see cref="TelemetryIngestionOptions.MaxQueuedSpans"/>.</summary>
    public RecordCountGate TraceGate { get; }

    /// <summary>Gate on resident METRICS. See <see cref="TelemetryIngestionOptions.MaxQueuedMetrics"/>.</summary>
    public RecordCountGate MetricGate { get; }

    // Enqueue timestamp per queued export, keyed by the export's list instance. A side table rather
    // than a wrapper type so the channels keep carrying plain lists; an export written without a
    // stamp (a test writing straight to a channel) simply records no commit lag.
    private readonly ConditionalWeakTable<object, EnqueueStamp> _enqueued = new();

    private sealed class EnqueueStamp(long timestamp) { public readonly long Timestamp = timestamp; }

    /// <summary>
    /// Stamps <paramref name="export"/> as enqueued now. Write repositories call this immediately
    /// before writing to a channel; <see cref="TelemetryIngestionWorker"/> reads the stamp back with
    /// <see cref="TryTakeEnqueued"/> to measure commit lag.
    /// </summary>
    public void MarkEnqueued(object export) => _enqueued.AddOrUpdate(export, new EnqueueStamp(Stopwatch.GetTimestamp()));

    /// <summary>The stamp's <see cref="Stopwatch"/> timestamp, removing it. False if the export was never stamped.</summary>
    public bool TryTakeEnqueued(object export, out long timestamp)
    {
        if (_enqueued.TryGetValue(export, out var stamp))
        {
            _enqueued.Remove(export);
            timestamp = stamp.Timestamp;
            return true;
        }
        timestamp = 0;
        return false;
    }

    public TelemetryIngestionChannel(IOptions<TelemetryIngestionOptions> options, IngestionMetrics metrics)
    {
        var o = options.Value;
        LogGate = new RecordCountGate(o.MaxQueuedLogRecords, metrics, "logs");
        TraceGate = new RecordCountGate(o.MaxQueuedSpans, metrics, "traces");
        MetricGate = new RecordCountGate(o.MaxQueuedMetrics, metrics, "metrics");

        // The channel owns the gates, so it hands their resident counts to the gauge.
        metrics.RegisterResidentRecords("logs", () => LogGate.Resident);
        metrics.RegisterResidentRecords("traces", () => TraceGate.Resident);
        metrics.RegisterResidentRecords("metrics", () => MetricGate.Resident);
    }

    /// <summary>
    /// Refuses further writes on all three channels -- called by
    /// <see cref="TelemetryIngestionWorker.StopAsync"/> at host shutdown, so a late enqueue throws
    /// <see cref="ChannelClosedException"/> (surfaced to the client as gRPC <c>UNAVAILABLE</c>)
    /// instead of landing in a queue the stopping worker will never drain. Items already queued
    /// remain readable.
    /// </summary>
    public void CompleteWriters()
    {
        Logs.Writer.TryComplete();
        Traces.Writer.TryComplete();
        Metrics.Writer.TryComplete();
    }
}
