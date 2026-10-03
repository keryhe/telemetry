using Grpc.Core;
using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Core;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.CollectorAuth;

/// <summary>
/// The collector's API key authentication (collector-authentication plan, Phases 0, 3 and 4), in process: real gRPC over
/// TestServer, the real registration, a fake key lookup and a controllable clock. Every rejection case has a
/// negative control, the same call with a valid key, in <see cref="Valid_key_is_accepted_and_every_record_carries_the_keys_tenant"/>.
/// </summary>
[Trait("Suite", "CollectorAuth")]
public class CollectorAuthTests
{
    private const string Key = "ktel_test-key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherKey = "ktel_test-key-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly string[] Signals = ["traces", "logs", "metrics"];

    private static Metadata Bearer(string key) => new() { { "authorization", $"Bearer {key}" } };

    /// <summary>Exports one record of the signal; throws <see cref="RpcException"/> on a non-OK status.</summary>
    private static async Task ExportAsync(CollectorHost host, string signal, Metadata? headers)
    {
        switch (signal)
        {
            case "traces":
                await new OpenTelemetry.Proto.Collector.Trace.V1.TraceService.TraceServiceClient(host.Channel).ExportAsync(TraceRequest(), headers);
                break;
            case "logs":
                await new OpenTelemetry.Proto.Collector.Logs.V1.LogsService.LogsServiceClient(host.Channel).ExportAsync(LogsRequest(), headers);
                break;
            default:
                await new OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService.MetricsServiceClient(host.Channel).ExportAsync(MetricsRequest(), headers);
                break;
        }
    }

    private static ExportTraceServiceRequest TraceRequest()
    {
        var scope = new ScopeSpans();
        scope.Spans.Add(new Span
        {
            TraceId = ByteString.CopyFrom(new byte[16].Select((_, i) => (byte)(i + 1)).ToArray()),
            SpanId = ByteString.CopyFrom(new byte[8].Select((_, i) => (byte)(i + 1)).ToArray()),
            Name = "op", StartTimeUnixNano = 1, EndTimeUnixNano = 2
        });
        var rs = new ResourceSpans { Resource = Res() };
        rs.ScopeSpans.Add(scope);
        var r = new ExportTraceServiceRequest();
        r.ResourceSpans.Add(rs);
        return r;
    }

    private static ExportLogsServiceRequest LogsRequest()
    {
        var scope = new ScopeLogs();
        scope.LogRecords.Add(new LogRecord { TimeUnixNano = 1, Body = new AnyValue { StringValue = "hi" } });
        var rl = new ResourceLogs { Resource = Res() };
        rl.ScopeLogs.Add(scope);
        var r = new ExportLogsServiceRequest();
        r.ResourceLogs.Add(rl);
        return r;
    }

    private static ExportMetricsServiceRequest MetricsRequest()
    {
        var gauge = new Gauge();
        gauge.DataPoints.Add(new NumberDataPoint { TimeUnixNano = 1, AsInt = 1 });
        var scope = new ScopeMetrics();
        scope.Metrics.Add(new Metric { Name = "m", Gauge = gauge });
        var rm = new ResourceMetrics { Resource = Res() };
        rm.ScopeMetrics.Add(scope);
        var r = new ExportMetricsServiceRequest();
        r.ResourceMetrics.Add(rm);
        return r;
    }

    private static OpenTelemetry.Proto.Resource.V1.Resource Res()
    {
        var res = new OpenTelemetry.Proto.Resource.V1.Resource();
        res.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "svc" } });
        return res;
    }

    /// <summary>The tenant of every record the signal's channel holds (and drains it); empty when nothing got in.</summary>
    private static List<long> TenantsEnqueued(CollectorHost host, string signal)
    {
        var tenants = new List<long>();
        switch (signal)
        {
            case "traces":
                while (host.Ingestion.Traces.Reader.TryRead(out var b)) tenants.AddRange(b.Select(x => x.Resource!.TenantId));
                break;
            case "logs":
                while (host.Ingestion.Logs.Reader.TryRead(out var b)) tenants.AddRange(b.Select(x => x.Resource!.TenantId));
                break;
            default:
                while (host.Ingestion.Metrics.Reader.TryRead(out var b)) tenants.AddRange(b.Select(x => x.Resource!.TenantId));
                break;
        }
        return tenants;
    }

    private static async Task<RpcException> RejectedAsync(CollectorHost host, string signal, Metadata? headers) =>
        await Assert.ThrowsAsync<RpcException>(() => ExportAsync(host, signal, headers));

    // ── 1: every rejection is a real UNAUTHENTICATED, before the service ─────────────────────────

    public static IEnumerable<object?[]> Rejections()
    {
        foreach (var s in Signals)
        {
            yield return [s, null, "missing"];
            yield return [s, "Basic dXNlcjpwYXNz", "malformed"];
            yield return [s, "Bearer ", "malformed"];
            yield return [s, "Bearer ktel_unknown", "invalid"];
            yield return [s, "Bearer " + "inactive", "invalid"]; // an inactive key is simply not returned by the lookup
        }
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task Bad_credentials_are_UNAUTHENTICATED_with_a_reason_and_never_reach_the_service(string signal, string? header, string reason)
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, 1);

        var headers = header is null ? new Metadata() : new Metadata { { "authorization", header } };
        var ex = await RejectedAsync(host, signal, headers);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.Contains(reason, ex.Status.Detail);
        Assert.Empty(TenantsEnqueued(host, signal));
        Assert.Equal(1, host.AuthFailureCount(signal, reason));
    }

    // ── 2 + 3: valid keys, no cross-talk; the negative control of 1 ─────────────────────────────────

    [Theory]
    [InlineData("traces")]
    [InlineData("logs")]
    [InlineData("metrics")]
    public async Task Valid_key_is_accepted_and_every_record_carries_the_keys_tenant(string signal)
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, tenantId: 11);
        host.Lookup.Add(OtherKey, tenantId: 22);

        await ExportAsync(host, signal, Bearer(Key));
        await ExportAsync(host, signal, Bearer(OtherKey));
        await ExportAsync(host, signal, Bearer(Key));

        Assert.Equal([11L, 22L, 11L], TenantsEnqueued(host, signal));
        Assert.Equal(0, host.TotalAuthFailures);
    }

    [Fact]
    public async Task Old_unprefixed_keys_still_authenticate()
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add("5Qv9oZ-legacy-key-with-no-prefix_xxxxxxxxxxxxx", tenantId: 3);
        await ExportAsync(host, "traces", Bearer("5Qv9oZ-legacy-key-with-no-prefix_xxxxxxxxxxxxx"));
        Assert.Equal([3L], TenantsEnqueued(host, "traces"));
    }

    // ── 4: expiry ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expired_key_is_rejected_as_expired_and_stays_expired_while_negative_cached()
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, 1, expiresAt: host.Clock.GetUtcNow().AddHours(-1));

        var first = await RejectedAsync(host, "traces", Bearer(Key));
        var second = await RejectedAsync(host, "traces", Bearer(Key));

        Assert.Equal(StatusCode.Unauthenticated, first.StatusCode);
        Assert.Contains("expired", first.Status.Detail);
        Assert.Contains("expired", second.Status.Detail); // served from the negative cache, reason kept
        Assert.Equal(1, host.Lookup.Calls);
        Assert.Equal(2, host.AuthFailureCount("traces", "expired"));
        Assert.Equal(0, host.AuthFailureCount("traces", "invalid"));
        Assert.Empty(TenantsEnqueued(host, "traces"));
    }

    [Fact]
    public async Task A_key_stops_exactly_at_its_expiry_even_while_its_positive_cache_entry_is_live()
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, 5, expiresAt: host.Clock.GetUtcNow().AddMinutes(10));

        await ExportAsync(host, "logs", Bearer(Key)); // resolved and cached before the expiry
        Assert.Equal([5L], TenantsEnqueued(host, "logs"));

        host.Clock.Advance(TimeSpan.FromMinutes(11));
        var ex = await RejectedAsync(host, "logs", Bearer(Key)); // positive entry (30 s) is still cached

        Assert.Contains("expired", ex.Status.Detail);
        Assert.Equal(1, host.Lookup.Calls);
        Assert.Empty(TenantsEnqueued(host, "logs"));
    }

    // ── 5: revocation ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_revoked_key_stops_within_the_positive_cache_ttl()
    {
        await using var host = await CollectorHost.StartAsync(new() { ["Telemetry:TenantResolution:PositiveCacheTtlSeconds"] = "1" });
        host.Lookup.Add(Key, 1);

        await ExportAsync(host, "metrics", Bearer(Key));
        host.Lookup.Remove(Key);
        await Task.Delay(TimeSpan.FromMilliseconds(1300));

        var ex = await RejectedAsync(host, "metrics", Bearer(Key));
        Assert.Contains("invalid", ex.Status.Detail);
    }

    // ── 6: lookup failure ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failing_lookup_is_UNAVAILABLE_not_cached_and_recovers()
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, 1);
        host.Lookup.Throw = true;

        var ex = await RejectedAsync(host, "traces", Bearer(Key));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Equal(1, host.AuthFailureCount("traces", "unavailable"));
        Assert.Contains(host.LogLines, l => l.StartsWith("Error") && l.Contains("database unreachable"));

        host.Lookup.Throw = false;
        await ExportAsync(host, "traces", Bearer(Key)); // nothing was negative-cached
        Assert.Equal([1L], TenantsEnqueued(host, "traces"));
    }

    // ── 7: counters and logs ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Failures_are_counted_by_signal_and_reason_and_the_key_never_reaches_the_logs()
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, 1, expiresAt: host.Clock.GetUtcNow().AddHours(-1));
        const string unknown = "ktel_secret-unknown-key-do-not-log-me-xxxx";

        await RejectedAsync(host, "traces", null);
        await RejectedAsync(host, "logs", Bearer(unknown));
        await RejectedAsync(host, "logs", Bearer(unknown));
        await RejectedAsync(host, "metrics", Bearer(Key));

        Assert.Equal(1, host.AuthFailureCount("traces", "missing"));
        Assert.Equal(2, host.AuthFailureCount("logs", "invalid"));
        Assert.Equal(1, host.AuthFailureCount("metrics", "expired"));
        Assert.Equal(4, host.TotalAuthFailures);

        Assert.Contains(host.LogLines, l => l.Contains("Rejected logs export") && l.Contains(ApiKeyAuthenticationHandler.ComputeKeyHash(unknown)[..8]));
        Assert.DoesNotContain(host.LogLines, l => l.Contains(unknown) || l.Contains(Key) || l.Contains(ApiKeyAuthenticationHandler.ComputeKeyHash(unknown)));
    }

    // ── 8: the handler acts only on collector endpoints ──────────────────────────────────────────────

    [Fact]
    public async Task A_non_collector_endpoint_is_left_alone_even_with_a_valid_key()
    {
        await using var host = await CollectorHost.StartAsync(map: app =>
            app.MapGet("/plain", (HttpContext c) => c.User.Identity?.IsAuthenticated == true ? "authenticated" : "anonymous"));
        host.Lookup.Add(Key, 1);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/plain");
        request.Headers.Add("Authorization", $"Bearer {Key}");
        var response = await host.Http.SendAsync(request);
        using var bad = new HttpRequestMessage(HttpMethod.Get, "/plain");
        bad.Headers.Add("Authorization", "Bearer nope");
        var badResponse = await host.Http.SendAsync(bad);

        Assert.Equal("anonymous", await response.Content.ReadAsStringAsync());
        Assert.Equal("anonymous", await badResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Lookup.Calls);
        Assert.Equal(0, host.TotalAuthFailures);
    }
}
