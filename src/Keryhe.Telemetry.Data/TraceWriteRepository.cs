using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

public class TraceWriteRepository : ITraceWriteRepository
{
    private readonly TelemetryIngestionChannel _channel;
    private readonly ITelemetryWriteStore _store;
    private readonly ILogger<TraceWriteRepository> _logger;

    public TraceWriteRepository(
        TelemetryIngestionChannel channel,
        ITelemetryWriteStore store,
        ILogger<TraceWriteRepository> logger)
    {
        _channel = channel;
        _store = store;
        _logger = logger;
    }

    public async Task<string> StoreTraceAsync(
        TraceModel trace,
        CancellationToken cancellationToken = default)
    {
        if (trace == null) throw new ArgumentNullException(nameof(trace));
        if (!trace.Spans.Any()) throw new ArgumentException("Trace must contain at least one span");
        await _channel.Traces.Writer.WriteAsync([trace], cancellationToken);
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
        await _channel.Traces.Writer.WriteAsync([trace], cancellationToken);
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
        await _channel.Traces.Writer.WriteAsync(list, cancellationToken);
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
        await _channel.Traces.Writer.WriteAsync(traces, cancellationToken);
        return Enumerable.Empty<long>();
    }

    public Task<bool> DeleteTraceAsync(string traceIdHex, CancellationToken cancellationToken = default)
        => _store.DeleteTraceAsync(traceIdHex, cancellationToken);

    public Task<int> DeleteTracesByTimeRangeAsync(
        DateTime startTime,
        DateTime endTime,
        CancellationToken cancellationToken = default)
        => _store.DeleteTracesByTimeRangeAsync(startTime, endTime, cancellationToken);

    public Task<bool> DeleteSpanAsync(
        string traceIdHex,
        string spanIdHex,
        CancellationToken cancellationToken = default)
        => _store.DeleteSpanAsync(traceIdHex, spanIdHex, cancellationToken);
}
