using Google.Protobuf;
using Grpc.Core;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using Keryhe.Telemetry.Collector.Services;

namespace Keryhe.Telemetry.IntegrationTests.CollectorAuth;

/// <summary>
/// Collector improvements phase 3, in process (TestServer, no database, a fake key lookup that counts its calls): one tenant cannot fill the
/// queue for the others, a tenant's rate is limited, failed authentication is limited per client address without touching valid clients, and
/// the control-plane lookups for uncached keys are coalesced and capped.
/// </summary>
[Trait("Suite", "CollectorAuth")]
public class TenantIsolationTests
{
    private const string KeyA = "ktel_tenant-a-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string KeyB = "ktel_tenant-b-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static Metadata Bearer(string key, string? ip = null)
    {
        var m = new Metadata { { "authorization", $"Bearer {key}" } };
        if (ip is not null) m.Add("X-Test-IP", ip);
        return m;
    }

    private static OpenTelemetry.Proto.Collector.Trace.V1.TraceService.TraceServiceClient Client(CollectorHost h) => new(h.Channel);

    private static ExportTraceServiceRequest Spans(int count)
    {
        var scope = new ScopeSpans();
        for (var i = 0; i < count; i++)
            scope.Spans.Add(new Span
            {
                TraceId = ByteString.CopyFrom(new byte[16].Select((_, k) => (byte)(k + 1)).ToArray()),
                SpanId = ByteString.CopyFrom(BitConverter.GetBytes((long)i + 1)),
                Name = "op", StartTimeUnixNano = 1, EndTimeUnixNano = 2
            });
        var rs = new ResourceSpans { Resource = new OpenTelemetry.Proto.Resource.V1.Resource() };
        rs.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "svc" } });
        rs.ScopeSpans.Add(scope);
        var req = new ExportTraceServiceRequest();
        req.ResourceSpans.Add(rs);
        return req;
    }

    private static Dictionary<string, string?> Config(Dictionary<string, string?>? extra = null)
    {
        var c = new Dictionary<string, string?>
        {
            ["Telemetry:Ingestion:MaxQueuedSpans"] = "100",
            ["Telemetry:Ingestion:MaxGateWaitMilliseconds"] = "50",
        };
        if (extra is not null) foreach (var (k, v) in extra) c[k] = v;
        return c;
    }

    // ---- fair use between tenants ----

    [Fact]
    public async Task A_tenant_over_its_share_is_refused_while_another_tenants_export_is_admitted()
    {
        await using var host = await CollectorHost.StartAsync(Config(new() { ["Telemetry:Ingestion:TenantQuota:MaxShare"] = "0.5" }));
        host.Lookup.Add(KeyA, tenantId: 1);
        host.Lookup.Add(KeyB, tenantId: 2);

        await Client(host).ExportAsync(Spans(40), Bearer(KeyA));                      // tenant 1 holds 40 of its 50
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(40), Bearer(KeyA)));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Contains("tenant_quota", ex.Status.Detail);
        Assert.True(ex.Trailers.GetValueBytes(ExportRejections.DetailsTrailer) is not null);   // with RetryInfo

        await Client(host).ExportAsync(Spans(40), Bearer(KeyB));                      // the queue has room (80 of 100) and tenant 2 holds nothing
        var held = host.Ingestion.TraceGate.TenantResident();
        Assert.Equal(40, held[1]);
        Assert.Equal(40, held[2]);
    }

    [Fact]
    public async Task With_a_share_of_one_there_is_no_quota_and_one_tenant_can_use_the_whole_queue()
    {
        await using var host = await CollectorHost.StartAsync(Config(new() { ["Telemetry:Ingestion:TenantQuota:MaxShare"] = "1" }));
        host.Lookup.Add(KeyA, tenantId: 1);

        await Client(host).ExportAsync(Spans(60), Bearer(KeyA));
        await Client(host).ExportAsync(Spans(40), Bearer(KeyA));
        Assert.Equal(100, host.Ingestion.TraceGate.Resident);
    }

    [Fact]
    public async Task A_per_tenant_override_changes_that_tenants_share_only()
    {
        await using var host = await CollectorHost.StartAsync(Config(new()
        {
            ["Telemetry:Ingestion:TenantQuota:MaxShare"] = "0.3",
            ["Telemetry:Ingestion:TenantQuota:Overrides:2:MaxShare"] = "0.9",
        }));
        host.Lookup.Add(KeyA, tenantId: 1);
        host.Lookup.Add(KeyB, tenantId: 2);

        await Client(host).ExportAsync(Spans(25), Bearer(KeyA));
        await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(10), Bearer(KeyA)));   // 35 > 30
        await Client(host).ExportAsync(Spans(30), Bearer(KeyB));
        await Client(host).ExportAsync(Spans(30), Bearer(KeyB));                                                       // 60 <= 90
    }

    [Fact]
    public async Task A_tenant_over_its_rate_is_refused_with_the_time_until_tokens_return_and_others_are_unaffected()
    {
        await using var host = await CollectorHost.StartAsync(Config(new()
        {
            ["Telemetry:Ingestion:MaxQueuedSpans"] = "10000",
            ["Telemetry:Ingestion:TenantQuota:RecordsPerSecond"] = "50",
        }));
        host.Lookup.Add(KeyA, tenantId: 1);
        host.Lookup.Add(KeyB, tenantId: 2);

        await Client(host).ExportAsync(Spans(50), Bearer(KeyA));                       // the one-second burst
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(50), Bearer(KeyA)));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Contains("tenant_rate", ex.Status.Detail);
        var status = Google.Rpc.Status.Parser.ParseFrom(ex.Trailers.GetValueBytes(ExportRejections.DetailsTrailer));
        var delay = status.Details.Single().Unpack<Google.Rpc.RetryInfo>().RetryDelay.ToTimeSpan();
        Assert.InRange(delay.TotalSeconds, 0.5, 1.5);                                   // about a second of tokens, plus jitter

        await Client(host).ExportAsync(Spans(50), Bearer(KeyB));                       // another tenant has its own bucket
    }

    // ---- failed authentication ----

    [Fact]
    public async Task Random_keys_from_one_address_cost_a_burst_of_lookups_then_are_refused_without_one()
    {
        await using var host = await CollectorHost.StartAsync(Config(), fakeClientAddresses: true);
        host.Lookup.Add(KeyA, tenantId: 1);

        var codes = new List<StatusCode>();
        for (var i = 0; i < 1000; i++)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(1), Bearer($"ktel_random-{i}", "203.0.113.9")));
            codes.Add(ex.StatusCode);
        }

        Assert.InRange(host.Lookup.Calls, 20, 30);                                    // the burst, plus a few earned back while the loop ran
        Assert.Equal(StatusCode.Unauthenticated, codes[0]);
        Assert.Equal(StatusCode.ResourceExhausted, codes[^1]);
        Assert.True(codes.Count(c => c == StatusCode.ResourceExhausted) > 950);
        Assert.Equal(1000, host.TotalAuthFailures);
        Assert.True(host.AuthFailureCount("traces", "throttled") > 950);
    }

    [Fact]
    public async Task A_valid_cached_key_still_works_from_an_address_that_has_used_up_its_failures()
    {
        await using var host = await CollectorHost.StartAsync(Config(), fakeClientAddresses: true);
        host.Lookup.Add(KeyA, tenantId: 1);

        await Client(host).ExportAsync(Spans(1), Bearer(KeyA, "203.0.113.9"));       // warms the positive cache
        for (var i = 0; i < 40; i++)
            await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(1), Bearer($"ktel_bad-{i}", "203.0.113.9")));

        await Client(host).ExportAsync(Spans(1), Bearer(KeyA, "203.0.113.9"));       // same address, exhausted, but a cached valid key
        // Control: an uncached key from that address is refused as throttled, and a different address is not affected.
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(1), Bearer("ktel_another", "203.0.113.9")));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        var other = await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(1), Bearer("ktel_another", "198.51.100.7")));
        Assert.Equal(StatusCode.Unauthenticated, other.StatusCode);
    }

    [Fact]
    public async Task A_refused_for_failures_response_has_no_retry_info_so_exporters_do_not_retry()
    {
        await using var host = await CollectorHost.StartAsync(Config(), fakeClientAddresses: true);
        for (var i = 0; i < 25; i++)
            await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(1), Bearer($"ktel_bad-{i}", "203.0.113.9")));

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Client(host).ExportAsync(Spans(1), Bearer("ktel_bad-x", "203.0.113.9")));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        Assert.Null(ex.Trailers.GetValueBytes(ExportRejections.DetailsTrailer));
    }

    // ---- control-plane lookups ----

    [Fact]
    public async Task Many_concurrent_first_requests_with_one_valid_key_make_one_lookup()
    {
        await using var host = await CollectorHost.StartAsync(Config(new() { ["Telemetry:Ingestion:MaxQueuedSpans"] = "100000", ["Telemetry:Ingestion:TenantQuota:MaxShare"] = "1" }));
        host.Lookup.Add(KeyA, tenantId: 1);
        host.Lookup.Delay = TimeSpan.FromMilliseconds(300);                              // long enough for all 100 to arrive while it runs

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Client(host).ExportAsync(Spans(1), Bearer(KeyA)).ResponseAsync));

        Assert.Equal(1, host.Lookup.Calls);
    }

    [Fact]
    public async Task Lookups_of_different_uncached_keys_are_capped_and_one_that_cannot_start_is_unavailable()
    {
        await using var host = await CollectorHost.StartAsync(Config(new()
        {
            ["Telemetry:TenantResolution:MaxConcurrentLookups"] = "2",
            ["Telemetry:TenantResolution:LookupQueueTimeoutMilliseconds"] = "150",
        }), fakeClientAddresses: true);
        host.Lookup.Delay = TimeSpan.FromMilliseconds(800);

        var calls = Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            try { await Client(host).ExportAsync(Spans(1), Bearer($"ktel_slow-{i}", $"192.0.2.{i + 1}")); return StatusCode.OK; }
            catch (RpcException ex) { return ex.StatusCode; }
        })).ToList();
        var codes = await Task.WhenAll(calls);

        Assert.Equal(2, host.Lookup.MaxConcurrent);                                      // never more than the cap at once
        Assert.Equal(4, codes.Count(c => c == StatusCode.Unavailable));                   // the other four timed out waiting for a slot
        Assert.Equal(2, codes.Count(c => c == StatusCode.Unauthenticated));              // the two that ran found no such key
    }
}
