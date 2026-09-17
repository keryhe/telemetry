using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Core.Data.Write;

public class TraceWriteRepository : ITraceWriteRepository
{
    private readonly TelemetryIngestionChannel _channel;
    private readonly ILogger<TraceWriteRepository> _logger;

    public TraceWriteRepository(
        TelemetryIngestionChannel channel,
        ILogger<TraceWriteRepository> logger)
    {
        _channel = channel;
        _logger = logger;
    }

    public async Task<string> StoreTraceAsync(
        TraceModel trace,
        CancellationToken cancellationToken = default)
    {
        if (trace == null) throw new ArgumentNullException(nameof(trace));
        if (!trace.Spans.Any()) throw new ArgumentException("Trace must contain at least one span");
        await WriteTracesAsync([trace], trace.Spans.Count, cancellationToken);
        return trace.Spans.First().TraceIdHex;
    }

    public async Task<long> StoreSpanAsync(
        SpanModel span,
        CancellationToken cancellationToken = default)
    {
        if (span == null) throw new ArgumentNullException(nameof(span));
        var trace = new TraceModel
        {
            Spans = [span],
            Resource = span.Resource,
            InstrumentationScope = span.InstrumentationScope
        };
        await WriteTracesAsync([trace], 1, cancellationToken);
        return -1;
    }

    public async Task<IEnumerable<string>> StoreTracesBatchAsync(
        IEnumerable<TraceModel> traces,
        CancellationToken cancellationToken = default)
    {
        var list = (traces ?? throw new ArgumentNullException(nameof(traces)))
            .Where(t => t.Spans.Count > 0)
            .ToList();
        if (list.Count == 0) return [];
        await WriteTracesAsync(list, list.Sum(t => t.Spans.Count), cancellationToken);
        _logger.LogDebug("Enqueued {Count} traces for async write", list.Count);
        return list.Select(t => t.Spans.First().TraceIdHex);
    }

    public async Task<IEnumerable<long>> StoreSpansBatchAsync(
        IEnumerable<SpanModel> spans,
        CancellationToken cancellationToken = default)
    {
        var list = (spans ?? throw new ArgumentNullException(nameof(spans))).ToList();
        if (list.Count == 0) return [];
        var traces = list.Select(s => new TraceModel
        {
            Spans = [s],
            Resource = s.Resource,
            InstrumentationScope = s.InstrumentationScope
        }).ToList();
        await WriteTracesAsync(traces, list.Count, cancellationToken);
        return Enumerable.Empty<long>();
    }

    /// <summary>
    /// Reserves <paramref name="spanCount"/> spans on <see cref="TelemetryIngestionChannel.TraceGate"/>
    /// before writing — spans, not <see cref="TraceModel"/> instances, are the unit the gate
    /// bounds (see the gate's own doc comment). Releases on a failed write so a cancelled or
    /// otherwise-failed enqueue cannot leak the reservation forever.
    ///
    /// Flattens <paramref name="traces"/> into a flat span list here, the one place it needs to
    /// happen, instead of leaving every provider's bulk writer to re-flatten the same
    /// <c>TraceModel</c> grouping on every flush. Each span's effective resource/scope -- its own
    /// override if it has one, else its trace's -- is resolved onto the span right here too, so
    /// <see cref="ITelemetryBulkWriter.FlushTracesAsync"/> and everything downstream of it can read
    /// <c>SpanModel.Resource</c>/<c>InstrumentationScope</c> directly with no fallback logic of its
    /// own to duplicate.
    /// </summary>
    private async Task WriteTracesAsync(List<TraceModel> traces, int spanCount, CancellationToken cancellationToken)
    {
        var spans = new List<SpanModel>(spanCount);
        foreach (var trace in traces)
        {
            foreach (var span in trace.Spans)
            {
                span.Resource ??= trace.Resource;
                span.InstrumentationScope ??= trace.InstrumentationScope;
                spans.Add(span);
            }
        }

        await _channel.TraceGate.AcquireAsync(spanCount, cancellationToken);
        try
        {
            await _channel.Traces.Writer.WriteAsync(spans, cancellationToken);
        }
        catch
        {
            _channel.TraceGate.Release(spanCount);
            throw;
        }
    }
}
