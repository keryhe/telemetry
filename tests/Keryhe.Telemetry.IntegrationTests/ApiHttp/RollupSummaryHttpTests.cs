using System.Net;
using System.Reflection;
using System.Text.Json;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.ApiHttp;

/// <summary>
/// The rollup-backed <c>traces/summary</c> and <c>logs/summary</c> routes over a fake
/// <see cref="IRollupReadRepository"/> (plans/summary-rollups.md, API section): response shape, how the
/// window and parameters reach the repository, the timeout flag. No database, no Docker.
/// </summary>
public class RollupSummaryHttpTests
{
    private static readonly string Range = "start=2026-10-01T00:00:00Z&end=2026-10-01T01:00:00Z";

    private sealed class FakeRollups : IRollupReadRepository
    {
        public RollupQuery? RequestQuery, LogQuery;
        public bool TimedOut;
        public Task<RollupReadResult<RequestRollupAggregate>> GetRequestRollupAsync(RollupQuery query, CancellationToken ct = default)
        {
            RequestQuery = query;
            var bands = new long[DurationBands.Count];
            bands[4] = 3;
            return Task.FromResult(new RollupReadResult<RequestRollupAggregate>
            {
                TimedOut = TimedOut,
                Rows = TimedOut ? [] : [new RequestRollupAggregate
                {
                    BucketStartNano = query.StartNano, Service = "web", RequestCount = 3, ErrorCount = 1,
                    SumDurationNanos = 6_000_000, MaxDurationNanos = 3_000_000, Bands = bands
                }]
            });
        }

        public Task<RollupReadResult<LogRollupAggregate>> GetLogRollupAsync(RollupQuery query, CancellationToken ct = default)
        {
            LogQuery = query;
            return Task.FromResult(new RollupReadResult<LogRollupAggregate>
            {
                Rows = [new LogRollupAggregate { BucketStartNano = query.StartNano, SeverityNumber = 17, RecordCount = 7 }]
            });
        }
    }

    /// <summary>A stand-in for a repository the summary route does not use but its controller still takes.</summary>
    public class Unused : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }

    private static Task<ApiHost> Start(FakeRollups fake) => ApiHost.StartAsync(new ApiHost.Options
    {
        Services = s =>
        {
            s.AddSingleton<IRollupReadRepository>(fake);
            s.AddScoped(_ => DispatchProxy.Create<ITraceReadRepository, Unused>());
            s.AddScoped(_ => DispatchProxy.Create<ILogReadRepository, Unused>());
        }
    });

    [Fact]
    public async Task TracesSummary_ReturnsTheRollupShape_AndPlansWholeMinuteWindow()
    {
        var fake = new FakeRollups();
        await using var host = await Start(fake);

        var response = await host.GetAsync($"/api/tenants/1/traces/summary?{Range}&service=web&bucketCount=60");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(60, body.GetProperty("bucketSeconds").GetInt32());
        Assert.Equal(60, body.GetProperty("buckets").GetArrayLength());
        Assert.Equal(3, body.GetProperty("summary").GetProperty("count").GetInt64());
        Assert.Equal(1, body.GetProperty("summary").GetProperty("errorCount").GetInt64());
        Assert.Equal("web", body.GetProperty("services")[0].GetProperty("service").GetString());
        Assert.Equal(4, body.GetProperty("latency")[0].GetProperty("band").GetInt32());
        Assert.False(body.GetProperty("timedOut").GetBoolean());
        Assert.True(body.TryGetProperty("writtenThrough", out _));
        // The old shape is gone.
        Assert.False(body.TryGetProperty("listTotal", out _));
        Assert.False(body.TryGetProperty("latencyBuckets", out _));

        Assert.Equal("web", fake.RequestQuery!.Service);
        Assert.Equal(60, fake.RequestQuery.BucketSeconds);
        Assert.Equal(60_000_000_000L * 60, fake.RequestQuery.EndNano - fake.RequestQuery.StartNano);
    }

    [Fact]
    public async Task TracesSummary_TimedOut_IsFlaggedAndEmpty()
    {
        await using var host = await Start(new FakeRollups { TimedOut = true });
        var body = JsonDocument.Parse(await (await host.GetAsync($"/api/tenants/1/traces/summary?{Range}")).Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("timedOut").GetBoolean());
        Assert.Equal(0, body.GetProperty("buckets").GetArrayLength());
    }

    [Fact]
    public async Task Summaries_RejectAnInvertedRange()
    {
        await using var host = await Start(new FakeRollups());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/tenants/1/traces/summary?start=2026-10-01T01:00:00Z&end=2026-10-01T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/tenants/1/logs/summary?start=2026-10-01T01:00:00Z&end=2026-10-01T00:00:00Z")).StatusCode);
    }

    [Fact]
    public async Task LogsSummary_ReturnsBucketsBySeverityGroup_AndPassesServiceAndMinSeverity()
    {
        var fake = new FakeRollups();
        await using var host = await Start(fake);

        var response = await host.GetAsync($"/api/tenants/1/logs/summary?{Range}&service=web&minSeverity=13&bucketCount=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(7, body.GetProperty("total").GetInt64());
        Assert.Equal(120, body.GetProperty("bucketSeconds").GetInt32());
        Assert.Equal(7, body.GetProperty("buckets")[0].GetProperty("error").GetInt64());
        Assert.Equal("web", fake.LogQuery!.Service);
        Assert.Equal(13, fake.LogQuery.MinSeverity);
    }

    [Fact]
    public async Task OldSummaryParameters_AreIgnored_NotRejected()
    {
        await using var host = await Start(new FakeRollups());
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/tenants/1/traces/summary?{Range}&mode=slow&q=x&asOf=2026-10-01T00:30:00Z&operation=o&minDurationMs=1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/tenants/1/logs/summary?{Range}&q=boom&asOf=2026-10-01T00:30:00Z")).StatusCode);
    }
}
