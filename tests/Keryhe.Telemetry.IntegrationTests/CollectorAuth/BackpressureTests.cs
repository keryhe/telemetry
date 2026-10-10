using System.Net;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Keryhe.Telemetry.Collector.Services;
using Keryhe.Telemetry.Core.Data;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.CollectorAuth;

/// <summary>
/// Collector improvements phase 1, in process (TestServer, no database): a saturated queue answers UNAVAILABLE with a
/// parsable RetryInfo inside the bounded wait, the pre-check refuses before conversion, the health endpoints answer without a key
/// and follow the gate and the control plane. The ingestion workers are not started, so a filled queue stays filled.
/// </summary>
[Trait("Suite", "CollectorAuth")]
public class BackpressureTests
{
    private const string Key = "ktel_test-key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const int ManagementPort = 5119;

    private static Dictionary<string, string?> Config(int maxWaitMs, int capacity = 2, int saturatedSeconds = 10) => new()
    {
        ["Telemetry:Ingestion:MaxQueuedSpans"] = capacity.ToString(),
        ["Telemetry:Ingestion:TenantQuota:MaxShare"] = "1",   // these tests are about the whole queue, not one tenant's share of it
        ["Telemetry:Ingestion:MaxGateWaitMilliseconds"] = maxWaitMs.ToString(),
        ["Telemetry:Ingestion:RejectRetryDelayMilliseconds"] = "2000",
        ["Telemetry:Ingestion:ReadinessSaturatedSeconds"] = saturatedSeconds.ToString(),
        ["Telemetry:Collector:ManagementPort"] = ManagementPort.ToString()
    };

    private static Metadata Bearer() => new() { { "authorization", $"Bearer {Key}" } };

    private static ExportTraceServiceRequest Spans(int count, bool valid = true)
    {
        var scope = new ScopeSpans();
        for (var i = 0; i < count; i++)
            scope.Spans.Add(new Span
            {
                TraceId = valid ? ByteString.CopyFrom(new byte[16].Select((_, k) => (byte)(k + 1)).ToArray()) : ByteString.Empty,
                SpanId = valid ? ByteString.CopyFrom(new byte[8].Select((_, k) => (byte)(k + i + 1)).ToArray()) : ByteString.Empty,
                Name = "op", StartTimeUnixNano = 1, EndTimeUnixNano = 2
            });
        var rs = new ResourceSpans { Resource = new OpenTelemetry.Proto.Resource.V1.Resource() };
        rs.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "svc" } });
        rs.ScopeSpans.Add(scope);
        var req = new ExportTraceServiceRequest();
        req.ResourceSpans.Add(rs);
        return req;
    }

    private static OpenTelemetry.Proto.Collector.Trace.V1.TraceService.TraceServiceClient Client(CollectorHost host) => new(host.Channel);

    private static TimeSpan RetryDelayOf(RpcException ex)
    {
        var details = ex.Trailers.GetValueBytes(ExportRejections.DetailsTrailer);
        Assert.NotNull(details);
        var status = Google.Rpc.Status.Parser.ParseFrom(details);
        Assert.Equal((int)StatusCode.Unavailable, status.Code);
        return status.Details.Single().Unpack<Google.Rpc.RetryInfo>().RetryDelay.ToTimeSpan();
    }

    [Fact]
    public async Task A_full_queue_answers_unavailable_with_retry_info_inside_the_bounded_wait()
    {
        await using var host = await CollectorHost.StartAsync(Config(maxWaitMs: 300));
        host.Lookup.Add(Key, tenantId: 7);

        await Client(host).ExportAsync(Spans(2), Bearer());            // fills the 2-span queue (control: room means accepted)

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(1), Bearer()));
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.InRange(RetryDelayOf(ex).TotalMilliseconds, 2000, 3000);   // 2 s base plus up to 50% jitter
        Assert.InRange(elapsed.TotalMilliseconds, 250, 3000);             // waited the bound, did not hang until a client deadline
        Assert.Equal(2, host.Ingestion.TraceGate.Resident);               // the refused export reserved nothing and enqueued nothing
    }

    [Fact]
    public async Task Room_appearing_inside_the_wait_admits_the_export()
    {
        await using var host = await CollectorHost.StartAsync(Config(maxWaitMs: 5000));
        host.Lookup.Add(Key, tenantId: 7);
        await Client(host).ExportAsync(Spans(2), Bearer());

        _ = Task.Run(async () => { await Task.Delay(200); host.Ingestion.TraceGate.Release(2); });
        await Client(host).ExportAsync(Spans(1), Bearer());
    }

    [Fact]
    public async Task The_precheck_refuses_before_the_request_reaches_the_repository()
    {
        // A refusal from the repository's bounded wait starts the gate's saturation clock (SaturatedFor); the pre-check, which runs
        // before the request is converted, touches no gate. So a refusal that leaves the clock at zero never got that far.
        await using var viaPrecheck = await CollectorHost.StartAsync(Config(maxWaitMs: 0));
        viaPrecheck.Lookup.Add(Key, tenantId: 7);
        await Client(viaPrecheck).ExportAsync(Spans(2), Bearer());
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Client(viaPrecheck).ExportAsync(Spans(1), Bearer()));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        RetryDelayOf(ex);
        Assert.Equal(TimeSpan.Zero, viaPrecheck.Ingestion.TraceGate.SaturatedFor);

        // Control: with a wait, the first refusal comes from the repository and starts the clock.
        await using var viaRepository = await CollectorHost.StartAsync(Config(maxWaitMs: 50));
        viaRepository.Lookup.Add(Key, tenantId: 7);
        await Client(viaRepository).ExportAsync(Spans(2), Bearer());
        await Assert.ThrowsAsync<RpcException>(async () => await Client(viaRepository).ExportAsync(Spans(1), Bearer()));
        Assert.True(viaRepository.Ingestion.TraceGate.SaturatedFor > TimeSpan.Zero);
    }

    [Fact]
    public async Task Health_endpoints_answer_without_a_key_and_only_on_the_management_port()
    {
        await using var host = await CollectorHost.StartAsync(Config(maxWaitMs: 100));

        Assert.Equal(HttpStatusCode.OK, (await host.Http.GetAsync($"http://localhost:{ManagementPort}/healthz/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Http.GetAsync($"http://localhost:{ManagementPort}/healthz/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.GetAsync("http://localhost:5117/healthz/ready")).StatusCode);
    }

    [Fact]
    public async Task Readiness_fails_when_the_queue_stays_full_and_recovers_when_it_drains()
    {
        await using var host = await CollectorHost.StartAsync(Config(maxWaitMs: 50, saturatedSeconds: 1));
        host.Lookup.Add(Key, tenantId: 7);
        await Client(host).ExportAsync(Spans(2), Bearer());
        await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(1), Bearer()));   // first refusal starts the clock

        await Task.Delay(1200);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Http.GetAsync($"http://localhost:{ManagementPort}/healthz/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Http.GetAsync($"http://localhost:{ManagementPort}/healthz/live")).StatusCode);

        host.Ingestion.TraceGate.Release(2);
        Assert.Equal(HttpStatusCode.OK, (await host.Http.GetAsync($"http://localhost:{ManagementPort}/healthz/ready")).StatusCode);
    }

    [Fact]
    public async Task Readiness_follows_the_control_plane_lookup_failures()
    {
        await using var host = await CollectorHost.StartAsync(Config(maxWaitMs: 100));
        host.Lookup.Add(Key, tenantId: 7);
        var ready = $"http://localhost:{ManagementPort}/healthz/ready";

        host.Lookup.Throw = true;
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(1), Bearer()));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Http.GetAsync(ready)).StatusCode);   // failing, but not yet for ReadinessControlPlaneSeconds

        host.Clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Http.GetAsync(ready)).StatusCode);

        host.Lookup.Throw = false;
        await Client(host).ExportAsync(Spans(1), Bearer());
        Assert.Equal(HttpStatusCode.OK, (await host.Http.GetAsync(ready)).StatusCode);
    }

    [Fact]
    public async Task Refused_and_accepted_records_are_counted_by_tenant_and_reason()
    {
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == "Keryhe.Telemetry.Ingestion") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) =>
        {
            var parts = tags.ToArray().Where(t => t.Key is "tenant" or "reason").Select(t => $"{t.Key}={t.Value}");
            seen.AddOrUpdate($"{i.Name.Split('.').Last()}|{string.Join(",", parts)}", v, (_, o) => o + v);
        });
        listener.Start();

        await using var host = await CollectorHost.StartAsync(Config(maxWaitMs: 50));
        host.Lookup.Add(Key, tenantId: 70707);
        await Client(host).ExportAsync(Spans(2), Bearer());
        await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(1), Bearer()));

        Assert.Equal(2, seen.GetValueOrDefault("records_accepted|tenant=70707"));
        Assert.Equal(1, seen.GetValueOrDefault("records_refused|tenant=70707,reason=throttled"));
    }
}
