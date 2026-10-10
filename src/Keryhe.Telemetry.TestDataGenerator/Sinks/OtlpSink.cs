using Grpc.Core;
using Grpc.Net.Client;
using Keryhe.Telemetry.TestDataGenerator.Clock;
using Keryhe.Telemetry.TestDataGenerator.Model;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Keryhe.Telemetry.TestDataGenerator.Sinks;

/// <summary>
/// The backfill sink: builds OTLP messages directly, so every timestamp is the simulation's, and sends them
/// over gRPC with the tenant's bearer key. Used for history only; live data goes through the real SDK.
/// </summary>
public sealed class OtlpSink : ISink
{
    private const int MaxLogsPerExport = 5000;
    private const int MaxAttempts = 12;

    private readonly string _tenant;
    private readonly int _maxSpansPerExport;
    private readonly ILogger _logger;
    private readonly GrpcChannel _channel;
    private readonly Metadata _headers;
    private readonly TraceService.TraceServiceClient _traces;
    private readonly LogsService.LogsServiceClient _logs;
    private readonly MetricsService.MetricsServiceClient _metrics;
    private MetricAggregator? _aggregator;
    private readonly int _seed;

    public OtlpSink(string tenant, string apiKey, Uri endpoint, int maxSpansPerExport, int seed, ILogger logger)
    {
        _tenant = tenant;
        _maxSpansPerExport = maxSpansPerExport;
        _seed = seed;
        _logger = logger;
        _channel = GrpcChannel.ForAddress(endpoint);
        _headers = new Metadata { { "authorization", $"Bearer {apiKey}" } };
        _traces = new TraceService.TraceServiceClient(_channel);
        _logs = new LogsService.LogsServiceClient(_channel);
        _metrics = new MetricsService.MetricsServiceClient(_channel);
    }

    public long SpansSent { get; private set; }
    public long LogsSent { get; private set; }

    public async Task WriteAsync(SimChunk chunk, CancellationToken ct)
    {
        _aggregator ??= new MetricAggregator(chunk.From, new SimRandom(SimRandom.Hash(_seed, _tenant, "exemplars")));

        var spans = chunk.AllSpans().ToList();
        foreach (var batch in spans.Chunk(_maxSpansPerExport))
        {
            var request = new ExportTraceServiceRequest();
            foreach (var perPod in batch.GroupBy(s => s.Instance.PodName))
            {
                var instance = perPod.First().Instance;
                var scope = new ScopeSpans { Scope = OtlpMapper.Scope(instance.PodName) };
                foreach (var s in perPod) scope.Spans.Add(OtlpMapper.ToSpan(s));
                var rs = new ResourceSpans { Resource = OtlpMapper.ResourceOf(instance) };
                rs.ScopeSpans.Add(scope);
                request.ResourceSpans.Add(rs);
            }
            await SendAsync("traces", t => _traces.ExportAsync(request, _headers, deadline: DateTime.UtcNow.AddSeconds(30), t).ResponseAsync
                .ContinueWith(r => r.Result.PartialSuccess?.RejectedSpans ?? 0, t), ct);
        }
        SpansSent += spans.Count;

        var logs = spans.SelectMany(s => s.Logs).Concat(chunk.BackgroundLogs).ToList();
        foreach (var batch in logs.Chunk(MaxLogsPerExport))
        {
            var request = new ExportLogsServiceRequest();
            foreach (var perPod in batch.GroupBy(l => l.Instance.PodName))
            {
                var rl = new ResourceLogs { Resource = OtlpMapper.ResourceOf(perPod.First().Instance) };
                foreach (var perCategory in perPod.GroupBy(l => l.Category))
                {
                    var scope = new ScopeLogs { Scope = OtlpMapper.Scope(perCategory.Key) };
                    foreach (var l in perCategory) scope.LogRecords.Add(OtlpMapper.ToLog(l));
                    rl.ScopeLogs.Add(scope);
                }
                request.ResourceLogs.Add(rl);
            }
            await SendAsync("logs", t => _logs.ExportAsync(request, _headers, deadline: DateTime.UtcNow.AddSeconds(30), t).ResponseAsync
                .ContinueWith(r => r.Result.PartialSuccess?.RejectedLogRecords ?? 0, t), ct);
        }
        LogsSent += logs.Count;

        foreach (var s in spans)
            foreach (var m in s.Measurements)
                _aggregator.Observe(s, m);
        foreach (var sample in chunk.Samples)
            _aggregator.Sample(sample);

        var metrics = _aggregator.Flush(chunk.To);
        if (metrics is not null)
        {
            await SendAsync("metrics", t => _metrics.ExportAsync(metrics, _headers, deadline: DateTime.UtcNow.AddSeconds(30), t).ResponseAsync
                .ContinueWith(r => r.Result.PartialSuccess?.RejectedDataPoints ?? 0, t), ct);
        }
    }

    /// <summary>Sends with exponential backoff on the statuses that mean "slow down" or "try again".</summary>
    private async Task SendAsync(string signal, Func<CancellationToken, Task<long>> call, CancellationToken ct)
    {
        TimeSpan? lastRetryAfter = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var rejected = await call(ct);
                if (rejected == 0) return;
                // Rejection is all-or-nothing per export: the collector's backlog gate said no.
                if (attempt >= MaxAttempts) throw new InvalidOperationException($"{_tenant}: collector kept rejecting {signal} ({rejected} records).");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                throw new InvalidOperationException(
                    $"Tenant '{_tenant}' was not authenticated by the collector ({ex.Status.Detail}). Check its API key.", ex);
            }
            catch (RpcException ex) when (attempt < MaxAttempts && ex.StatusCode is StatusCode.Unavailable or StatusCode.ResourceExhausted
                or StatusCode.DeadlineExceeded or StatusCode.Aborted or StatusCode.Internal)
            {
                lastRetryAfter = RetryAfterOf(ex);
                _logger.LogWarning("{Tenant}: {Signal} export failed ({Status}); retry {Attempt}{Told}", _tenant, signal, ex.StatusCode, attempt,
                    lastRetryAfter is { } told ? $" (collector asked for {told.TotalSeconds:0.#} s)" : "");
            }

            var delay = TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * Math.Pow(2, attempt - 1)) * (0.5 + Random.Shared.NextDouble() * 0.5));
            if (lastRetryAfter is { } asked) delay = asked > delay ? asked : delay;   // never come back sooner than the collector asked
            lastRetryAfter = null;
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>The <c>RetryInfo</c> delay a refused export carries in <c>grpc-status-details-bin</c>, if any.</summary>
    private static TimeSpan? RetryAfterOf(RpcException ex)
    {
        var bytes = ex.Trailers.GetValueBytes("grpc-status-details-bin");
        if (bytes is null) return null;
        try
        {
            foreach (var detail in Google.Rpc.Status.Parser.ParseFrom(bytes).Details)
                if (detail.Is(Google.Rpc.RetryInfo.Descriptor))
                    return detail.Unpack<Google.Rpc.RetryInfo>().RetryDelay?.ToTimeSpan();
        }
        catch (Google.Protobuf.InvalidProtocolBufferException) { }
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }
}
