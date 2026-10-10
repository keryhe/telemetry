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

    private sealed class EnqueueStamp(long timestamp, long bytes) { public readonly long Timestamp = timestamp; public readonly long Bytes = bytes; }

    /// <summary>
    /// Stamps <paramref name="export"/> as enqueued now. Write repositories call this immediately
    /// before writing to a channel; <see cref="TelemetryIngestionWorker"/> reads the stamp back with
    /// <see cref="TryTakeEnqueued"/> to measure commit lag.
    /// </summary>
    public void MarkEnqueued(object export, long bytes = 0) => _enqueued.AddOrUpdate(export, new EnqueueStamp(Stopwatch.GetTimestamp(), bytes));

    /// <summary>The stamp's <see cref="Stopwatch"/> timestamp, removing it. False if the export was never stamped.</summary>
    public bool TryTakeEnqueued(object export, out long timestamp) => TryTakeEnqueued(export, out timestamp, out _);

    /// <summary>
    /// As <see cref="TryTakeEnqueued(object, out long)"/>, also returning the bytes the export reserved on its gate (0 when the
    /// byte budget is off or the export was written without a size), which the worker releases with its records.
    /// </summary>
    public bool TryTakeEnqueued(object export, out long timestamp, out long bytes)
    {
        if (_enqueued.TryGetValue(export, out var stamp))
        {
            _enqueued.Remove(export);
            timestamp = stamp.Timestamp;
            bytes = stamp.Bytes;
            return true;
        }
        timestamp = 0;
        bytes = 0;
        return false;
    }

    private readonly TelemetryIngestionOptions _options;
    private readonly IOptionsMonitor<TenantQuotaOptions>? _quota;
    private readonly TenantRateLimiter _rates = new();
    private volatile bool _shuttingDown;

    /// <summary>True once the writers have been completed for host shutdown.</summary>
    public bool IsShuttingDown => _shuttingDown;

    /// <summary>The longest gate saturation, across signals, since the gate last had room.</summary>
    public TimeSpan LongestSaturation => new[] { LogGate.SaturatedFor, TraceGate.SaturatedFor, MetricGate.SaturatedFor }.Max();

    /// <summary>The delay a refused client is told to wait: the configured base plus up to 50% jitter.</summary>
    public TimeSpan NextRetryDelay()
    {
        var baseMs = _options.RejectRetryDelayMilliseconds;
        return TimeSpan.FromMilliseconds(baseMs * (1 + Random.Shared.NextDouble() * 0.5));
    }

    /// <summary>
    /// Refuses an export at once, before it is converted, when <paramref name="gate"/> is full and has been refusing callers
    /// for at least the whole bounded wait: an export arriving now would very likely wait that long and be refused anyway,
    /// so this spares the conversion and the held-open request. A gate that has only just filled does not trigger it (the
    /// next flush may free room within the wait). The bounded wait in <see cref="AcquireOrRejectAsync"/> still applies
    /// afterwards, when the exact count is known. A no-op when the wait is unbounded.
    /// </summary>
    public void ThrowIfSaturated(string signal, RecordCountGate gate)
    {
        if (_options.EffectiveMaxGateWait is { } wait && gate.IsSaturated && gate.SaturatedFor >= wait)
            throw new IngestionRejectedException(signal, RefusalReasons.Throttled, NextRetryDelay());
    }

    /// <summary>
    /// Reserves <paramref name="count"/> records (and <paramref name="bytes"/>) for <paramref name="tenantId"/> on <paramref name="gate"/>.
    /// First the tenant's rate limit (<see cref="TenantQuotaOptions.RecordsPerSecond"/>), which refuses at once with the time until enough
    /// tokens return; then the gate, waiting at most <see cref="TelemetryIngestionOptions.MaxGateWaitMilliseconds"/>. Throws
    /// <see cref="IngestionRejectedException"/> (reason <c>tenant_rate</c>, <c>tenant_quota</c> when the tenant stayed over its share of the
    /// queue, <c>throttled</c> when the whole queue stayed full) and reserves nothing in that case. Give the same tenant back with
    /// <see cref="Release"/>.
    /// </summary>
    public async Task AcquireOrRejectAsync(string signal, RecordCountGate gate, int count, CancellationToken ct, long bytes = 0, long tenantId = ResourceModel.DefaultTenantId)
    {
        var quota = _quota?.CurrentValue;
        if (quota is not null)
        {
            var rate = quota.RateFor(tenantId);
            if (rate > 0 && !_rates.TryAcquire(tenantId, signal, count, rate, out var retryAfter))
                throw new IngestionRejectedException(signal, RefusalReasons.TenantRate, Jitter(retryAfter));
        }

        var share = quota?.ShareFor(tenantId) ?? 1.0;
        var wait = _options.EffectiveMaxGateWait ?? System.Threading.Timeout.InfiniteTimeSpan;
        var result = await gate.TryAcquireAsync(tenantId, share, count, bytes, wait, ct);
        if (result != RecordCountGate.Result.Admitted)
            throw new IngestionRejectedException(signal,
                result == RecordCountGate.Result.TenantQuota ? RefusalReasons.TenantQuota : RefusalReasons.Throttled, NextRetryDelay());
    }

    /// <summary>Gives back a reservation made by <see cref="AcquireOrRejectAsync"/> that was never queued.</summary>
    public void Release(RecordCountGate gate, int count, long bytes, long tenantId)
    {
        var tally = new RecordCountGate.TenantTally();
        tally.Add(tenantId, count, bytes);
        gate.Release(tally);
    }

    private static TimeSpan Jitter(TimeSpan delay) => delay + TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Random.Shared.NextDouble() * 0.25);

    public TelemetryIngestionChannel(IOptions<TelemetryIngestionOptions> options, IngestionMetrics metrics, IOptionsMonitor<TenantQuotaOptions>? quota = null)
    {
        _quota = quota;
        var o = options.Value;
        _options = o;
        LogGate = new RecordCountGate(o.MaxQueuedLogRecords, metrics, "logs", o.MaxQueuedBytesPerSignal);
        TraceGate = new RecordCountGate(o.MaxQueuedSpans, metrics, "traces", o.MaxQueuedBytesPerSignal);
        MetricGate = new RecordCountGate(o.MaxQueuedMetrics, metrics, "metrics", o.MaxQueuedBytesPerSignal);

        // The channel owns the gates, so it hands their resident counts to the gauge.
        metrics.RegisterResidentRecords("logs", () => LogGate.Resident);
        metrics.RegisterResidentRecords("traces", () => TraceGate.Resident);
        metrics.RegisterResidentRecords("metrics", () => MetricGate.Resident);
        metrics.RegisterTenantResident("logs", LogGate.TenantResident);
        metrics.RegisterTenantResident("traces", TraceGate.TenantResident);
        metrics.RegisterTenantResident("metrics", MetricGate.TenantResident);
        metrics.RegisterResidentBytes("logs", () => LogGate.ResidentBytes);
        metrics.RegisterResidentBytes("traces", () => TraceGate.ResidentBytes);
        metrics.RegisterResidentBytes("metrics", () => MetricGate.ResidentBytes);
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
        _shuttingDown = true;
        Logs.Writer.TryComplete();
        Traces.Writer.TryComplete();
        Metrics.Writer.TryComplete();
    }
}
