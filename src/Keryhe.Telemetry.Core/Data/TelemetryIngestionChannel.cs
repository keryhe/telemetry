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

    public TelemetryIngestionChannel(IOptions<TelemetryIngestionOptions> options)
    {
        var o = options.Value;
        LogGate = new RecordCountGate(o.MaxQueuedLogRecords);
        TraceGate = new RecordCountGate(o.MaxQueuedSpans);
        MetricGate = new RecordCountGate(o.MaxQueuedMetrics);
    }
}
