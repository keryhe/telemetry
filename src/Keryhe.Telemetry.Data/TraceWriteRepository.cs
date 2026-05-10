using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data.Access;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

public class TraceWriteRepository : ITraceWriteRepository
{
    private readonly TelemetryWriteDbContext _context;
    private readonly TelemetryIngestionChannel _channel;
    private readonly ILogger<TraceWriteRepository> _logger;

    public TraceWriteRepository(
        TelemetryWriteDbContext context,
        TelemetryIngestionChannel channel,
        ILogger<TraceWriteRepository> logger)
    {
        _context = context;
        _channel = channel;
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

    public async Task<bool> DeleteTraceAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));

        var spans = await _context.Spans
            .Where(s => s.TraceId == traceIdHex)
            .ToListAsync(cancellationToken);

        if (spans.Count == 0) return false;

        _context.Spans.RemoveRange(spans);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> DeleteTracesByTimeRangeAsync(
        DateTime startTime,
        DateTime endTime,
        CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        var startNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime);
        var endNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime);

        var spans = await _context.Spans
            .Where(s => s.StartTimeUnixNano >= startNano && s.StartTimeUnixNano <= endNano)
            .ToListAsync(cancellationToken);

        var traceCount = spans.GroupBy(s => s.TraceId).Count();
        _context.Spans.RemoveRange(spans);
        await _context.SaveChangesAsync(cancellationToken);
        return traceCount;
    }

    public async Task<bool> DeleteSpanAsync(
        string traceIdHex,
        string spanIdHex,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (string.IsNullOrEmpty(spanIdHex))
            throw new ArgumentException("Span ID cannot be null or empty", nameof(spanIdHex));

        var span = await _context.Spans
            .FirstOrDefaultAsync(s => s.TraceId == traceIdHex && s.SpanId == spanIdHex, cancellationToken);

        if (span == null) return false;

        _context.Spans.Remove(span);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
