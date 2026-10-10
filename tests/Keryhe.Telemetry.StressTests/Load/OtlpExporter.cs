using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Keryhe.Telemetry.StressTests.Load;

/// <summary>The result of one Export call, as the client saw it.</summary>
public sealed record ExportResult(ExportOutcome Outcome, string Status, double LatencyMs, long Rejected, string? Error, int ThrottledAttempts = 0);

/// <summary>
/// Sends OTLP exports over gRPC with <c>Authorization: Bearer &lt;tenant key&gt;</c> and classifies
/// each result: OK, partial-success rejected (<c>rejected_*</c> &gt; 0), or a gRPC status failure.
/// Accepted records go into the sent ledger; rejected and failed ones are ledgered separately.
/// </summary>
public sealed class OtlpExporter : IAsyncDisposable
{
    private readonly Topology _topology;
    private readonly SentLedger _ledger;
    private readonly TimeSpan _timeout;
    private readonly List<GrpcChannel> _channels = [];
    private readonly List<LogsService.LogsServiceClient> _logClients = [];
    private readonly List<TraceService.TraceServiceClient> _traceClients = [];
    private readonly List<MetricsService.MetricsServiceClient> _metricClients = [];
    private readonly Metadata[] _headers;
    private long _next;

    public OtlpExporter(Uri grpcTarget, Topology topology, TransportLoad transport, SentLedger ledger)
    {
        _topology = topology;
        _ledger = ledger;
        _timeout = TimeSpan.FromSeconds(Math.Max(1, transport.ExportTimeoutSeconds));
        _headers = topology.Tenants.Select(t => new Metadata { { "authorization", $"Bearer {t.ApiKey}" } }).ToArray();

        for (var i = 0; i < Math.Max(1, transport.Channels); i++)
        {
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = transport.ConnectionsPerChannel > 1,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30)
            };
            var channel = GrpcChannel.ForAddress(grpcTarget, new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
            _channels.Add(channel);
            _logClients.Add(new LogsService.LogsServiceClient(channel));
            _traceClients.Add(new TraceService.TraceServiceClient(channel));
            _metricClients.Add(new MetricsService.MetricsServiceClient(channel));
        }
    }

    public Task<ExportResult> ExportLogsAsync(Payload<ExportLogsServiceRequest> p, bool redelivery, CancellationToken ct) =>
        SendAsync(p.TenantIndex, p.Entries, redelivery, async (headers, deadline, token) =>
        {
            var client = _logClients[(int)(Interlocked.Increment(ref _next) % _logClients.Count)];
            var response = await client.ExportAsync(p.Request, headers, deadline, token);
            return response.PartialSuccess?.RejectedLogRecords ?? 0;
        }, ct);

    public Task<ExportResult> ExportTracesAsync(Payload<ExportTraceServiceRequest> p, bool redelivery, CancellationToken ct) =>
        SendAsync(p.TenantIndex, p.Entries, redelivery, async (headers, deadline, token) =>
        {
            var client = _traceClients[(int)(Interlocked.Increment(ref _next) % _traceClients.Count)];
            var response = await client.ExportAsync(p.Request, headers, deadline, token);
            return response.PartialSuccess?.RejectedSpans ?? 0;
        }, ct);

    public Task<ExportResult> ExportMetricsAsync(Payload<ExportMetricsServiceRequest> p, bool redelivery, CancellationToken ct) =>
        SendAsync(p.TenantIndex, p.Entries, redelivery, async (headers, deadline, token) =>
        {
            var client = _metricClients[(int)(Interlocked.Increment(ref _next) % _metricClients.Count)];
            var response = await client.ExportAsync(p.Request, headers, deadline, token);
            return response.PartialSuccess?.RejectedDataPoints ?? 0;
        }, ct);

    private async Task<ExportResult> SendAsync(
        int tenantIndex, IReadOnlyList<LedgerEntry> entries, bool redelivery,
        Func<Metadata, DateTime, CancellationToken, Task<long>> call, CancellationToken ct)
    {
        var tenantId = _topology.Tenants[tenantIndex].Id;
        var ledgerEntries = redelivery ? entries.Select(e => e with { Redelivery = true }).ToList() : entries;
        var started = Stopwatch.GetTimestamp();
        var deadline = DateTime.UtcNow + _timeout;
        var throttled = 0;
        try
        {
            long rejected;
            while (true)
            {
                try { rejected = await call(_headers[tenantIndex], deadline, ct); break; }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable && ThrottleDelay(ex) is { } delay)
                {
                    // The collector's queue was full (UNAVAILABLE + RetryInfo): nothing was enqueued. Wait as told and send the same
                    // export again, as an OTLP exporter would, until the export's own deadline.
                    throttled++;
                    if (DateTime.UtcNow + delay >= deadline)
                    {
                        _ledger.RecordFailed(tenantId, ledgerEntries);
                        return new ExportResult(ExportOutcome.Throttled, "THROTTLED", Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, ex.Status.Detail, throttled);
                    }
                    await Task.Delay(delay, ct);
                }
            }
            var latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (rejected > 0)
            {
                // The server's rejection is all-or-nothing per export (see the collector services' Export docs).
                _ledger.RecordRejected(tenantId, ledgerEntries);
                return new ExportResult(ExportOutcome.Rejected, "OK_REJECTED", latency, rejected, null, throttled);
            }
            _ledger.RecordAccepted(tenantId, ledgerEntries);
            return new ExportResult(ExportOutcome.Ok, "OK", latency, 0, null, throttled);
        }
        catch (RpcException ex)
        {
            _ledger.RecordFailed(tenantId, ledgerEntries);
            // The client stopped waiting, but the server may already have enqueued the records (and will persist them).
            if (ex.StatusCode is StatusCode.DeadlineExceeded or StatusCode.Cancelled) _ledger.RecordMaybeLanded(tenantId, ledgerEntries);
            return new ExportResult(ExportOutcome.Failed, ex.StatusCode.ToString(), Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, ex.Status.Detail, throttled);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting the run down, not a server failure: left out of the accepted/failed ledger and latency, but the server may
            // already have enqueued it, so it is ledgered as possibly landed.
            _ledger.RecordMaybeLanded(tenantId, ledgerEntries);
            return new ExportResult(ExportOutcome.Failed, "CLIENT_CANCELLED", Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, null, throttled);
        }
    }

    /// <summary>The <c>RetryInfo</c> delay of a refusal (<c>grpc-status-details-bin</c>), or null when the status carries none.</summary>
    public static TimeSpan? ThrottleDelay(RpcException ex)
    {
        var bytes = ex.Trailers.GetValueBytes("grpc-status-details-bin");
        if (bytes is null) return null;
        try
        {
            var status = Google.Rpc.Status.Parser.ParseFrom(bytes);
            foreach (var detail in status.Details)
                if (detail.Is(Google.Rpc.RetryInfo.Descriptor))
                    return detail.Unpack<Google.Rpc.RetryInfo>().RetryDelay?.ToTimeSpan() ?? TimeSpan.FromSeconds(1);
        }
        catch (Google.Protobuf.InvalidProtocolBufferException) { }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var channel in _channels)
            await channel.ShutdownAsync();
        foreach (var channel in _channels)
            channel.Dispose();
    }
}
