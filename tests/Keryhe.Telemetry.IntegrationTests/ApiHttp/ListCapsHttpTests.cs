using System.Net;
using System.Reflection;
using System.Text.Json;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.ApiHttp;

/// <summary>
/// The capped list routes (plans/list-caps.md, API section) over recording fake repositories: the <c>order</c> and
/// <c>limit</c> parameters and how they reach the repository (the limit clamped to the configured cap), the
/// <c>{ items, truncated }</c> response shape, and the retired <c>page</c> routes and unbounded <c>GET logs</c> answering 404.
/// No database, no Docker.
/// </summary>
public class ListCapsHttpTests
{
    private static readonly string Range = "start=2026-10-01T00:00:00Z&end=2026-10-01T01:00:00Z";

    /// <summary>Records the arguments of each call by method name and answers every <c>Task&lt;T&gt;</c> with a default <c>T</c>.</summary>
    public class Recorder : DispatchProxy
    {
        public Dictionary<string, object?[]?> Calls { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls[targetMethod!.Name] = args;
            var returnType = targetMethod.ReturnType;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = returnType.GetGenericArguments()[0];
                var value = Activator.CreateInstance(inner);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
            }
            throw new NotSupportedException(targetMethod.Name);
        }
    }

    /// <summary>A stand-in for a repository the exercised route does not use but its controller still takes.</summary>
    public class Unused : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }

    private sealed record Fakes(Recorder Logs, Recorder Traces, Recorder Metrics);

    private static async Task<(ApiHost Host, Fakes Fakes)> Start(QueryLimitsOptions? limits = null)
    {
        var logs = DispatchProxy.Create<ILogReadRepository, Recorder>();
        var traces = DispatchProxy.Create<ITraceReadRepository, Recorder>();
        var metrics = DispatchProxy.Create<IMetricReadRepository, Recorder>();
        var host = await ApiHost.StartAsync(new ApiHost.Options
        {
            Services = s =>
            {
                s.AddSingleton(logs);
                s.AddSingleton(traces);
                s.AddSingleton(metrics);
                s.AddSingleton(DispatchProxy.Create<IRollupReadRepository, Unused>());
                if (limits is not null) s.AddSingleton(ProviderCapabilities.Default() with { Limits = limits });
            }
        });
        return (host, new Fakes((Recorder)(object)logs, (Recorder)(object)traces, (Recorder)(object)metrics));
    }

    private static T Arg<T>(Recorder recorder, string method) => (T)recorder.Calls[method]![0]!;

    // ── Logs ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LogsList_DefaultsToTheNewestRows_UpToTheConfiguredLimit()
    {
        var (host, fakes) = await Start();
        await using var disposeHost = host;

        var response = await host.GetAsync($"/api/tenants/1/logs/list?{Range}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Arg<LogQuery>(fakes.Logs, nameof(ILogReadRepository.GetLogListAsync));
        Assert.Equal(ListOrder.Newest, query.Order);
        Assert.Equal(1000, query.Limit);
    }

    [Theory]
    [InlineData("limit=5", 5)]
    [InlineData("limit=1000", 1000)]
    [InlineData("limit=999999", 1000)]   // never more than the configured limit
    [InlineData("limit=0", 1)]
    [InlineData("limit=-3", 1)]
    public async Task LogsList_ClampsTheLimitToOneThroughTheConfiguredLimit(string parameter, int expected)
    {
        var (host, fakes) = await Start();
        await using var disposeHost = host;

        await host.GetAsync($"/api/tenants/1/logs/list?{Range}&{parameter}");

        Assert.Equal(expected, Arg<LogQuery>(fakes.Logs, nameof(ILogReadRepository.GetLogListAsync)).Limit);
    }

    [Fact]
    public async Task LogsList_UsesTheConfiguredLimit_AsTheCeiling()
    {
        var (host, fakes) = await Start(new QueryLimitsOptions { Logs = 200 });
        await using var disposeHost = host;

        await host.GetAsync($"/api/tenants/1/logs/list?{Range}");
        Assert.Equal(200, Arg<LogQuery>(fakes.Logs, nameof(ILogReadRepository.GetLogListAsync)).Limit);

        await host.GetAsync($"/api/tenants/1/logs/list?{Range}&limit=500");
        Assert.Equal(200, Arg<LogQuery>(fakes.Logs, nameof(ILogReadRepository.GetLogListAsync)).Limit);
    }

    [Theory]
    [InlineData("oldest")]
    [InlineData("OLDEST")]
    public async Task LogsList_PassesTheOldestOrderThrough(string order)
    {
        var (host, fakes) = await Start();
        await using var disposeHost = host;

        var response = await host.GetAsync($"/api/tenants/1/logs/list?{Range}&order={order}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ListOrder.Oldest, Arg<LogQuery>(fakes.Logs, nameof(ILogReadRepository.GetLogListAsync)).Order);
    }

    [Fact]
    public async Task LogsList_RejectsAnUnknownOrder_AndAnInvertedWindow()
    {
        var (host, fakes) = await Start();
        await using var disposeHost = host;

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync($"/api/tenants/1/logs/list?{Range}&order=sideways")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/tenants/1/logs/list?start=2026-10-01T01:00:00Z&end=2026-10-01T00:00:00Z")).StatusCode);
        Assert.Empty(fakes.Logs.Calls);
    }

    [Fact]
    public async Task LogsList_ReturnsItemsAndTruncated_AndNoCursors()
    {
        var (host, _) = await Start();
        await using var disposeHost = host;

        var body = JsonDocument.Parse(await (await host.GetAsync($"/api/tenants/1/logs/list?{Range}")).Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(["items", "truncated"], body.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(body.GetProperty("truncated").GetBoolean());
    }

    // ── Traces ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TracesList_DefaultsToTheNewestTraces_UpToTheConfiguredLimit()
    {
        var (host, fakes) = await Start();
        await using var disposeHost = host;

        var response = await host.GetAsync($"/api/tenants/1/traces/list?{Range}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Arg<TraceQuery>(fakes.Traces, nameof(ITraceReadRepository.GetTraceListAsync));
        Assert.Equal(ListOrder.Newest, query.Order);
        Assert.Equal(500, query.Limit);
    }

    [Theory]
    [InlineData("limit=20", 20)]
    [InlineData("limit=100000", 500)]
    [InlineData("limit=0", 1)]
    public async Task TracesList_ClampsTheLimit(string parameter, int expected)
    {
        var (host, fakes) = await Start();
        await using var disposeHost = host;

        await host.GetAsync($"/api/tenants/1/traces/list?{Range}&{parameter}&order=oldest");

        var query = Arg<TraceQuery>(fakes.Traces, nameof(ITraceReadRepository.GetTraceListAsync));
        Assert.Equal(expected, query.Limit);
        Assert.Equal(ListOrder.Oldest, query.Order);
    }

    [Fact]
    public async Task TracesList_RejectsAnUnknownOrder_AndReturnsItemsAndTruncated()
    {
        var (host, _) = await Start();
        await using var disposeHost = host;

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync($"/api/tenants/1/traces/list?{Range}&order=sideways")).StatusCode);

        var body = JsonDocument.Parse(await (await host.GetAsync($"/api/tenants/1/traces/list?{Range}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["items", "truncated"], body.EnumerateObject().Select(p => p.Name).Order());
    }

    // ── Metrics ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", 500)]
    [InlineData("&limit=10", 10)]
    [InlineData("&limit=5000", 500)]
    public async Task MetricsCatalog_ClampsTheLimit_AndReturnsTruncated(string parameter, int expected)
    {
        var (host, fakes) = await Start();
        await using var disposeHost = host;

        var response = await host.GetAsync($"/api/tenants/1/metrics/catalog?{Range}{parameter}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, Arg<MetricCatalogQuery>(fakes.Metrics, nameof(IMetricReadRepository.GetMetricCatalogPageAsync)).Limit);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("truncated").GetBoolean());
        Assert.False(body.TryGetProperty("nextCursor", out _));
        Assert.False(body.TryGetProperty("total", out _));
    }

    [Theory]
    [InlineData("", 500)]
    [InlineData("&limit=25", 25)]
    [InlineData("&limit=5000", 500)]
    public async Task MetricsExemplars_ClampsTheLimit(string parameter, int expected)
    {
        var (host, fakes) = await Start();
        await using var disposeHost = host;

        // The fake answers a default page (not null), so the route is a 200.
        await host.GetAsync($"/api/tenants/1/metrics/exemplars?metricName=m&{Range}{parameter}");

        Assert.Equal(expected, Arg<MetricExemplarQuery>(fakes.Metrics, nameof(IMetricReadRepository.GetMetricExemplarsAsync)).Limit);
    }

    // ── Retired routes ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/tenants/1/logs/page?start=2026-10-01T00:00:00Z&end=2026-10-01T01:00:00Z&size=100&cursor=abc&nav=next")]
    [InlineData("/api/tenants/1/traces/page?start=2026-10-01T00:00:00Z&end=2026-10-01T01:00:00Z&size=100&cursor=abc&nav=next")]
    [InlineData("/api/tenants/1/logs?start=2026-10-01T00:00:00Z&end=2026-10-01T01:00:00Z")]
    public async Task RetiredRoutes_Answer404_InsteadOfSilentlyReturningTheFirstRows(string url)
    {
        var (host, fakes) = await Start();
        await using var disposeHost = host;

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync(url)).StatusCode);
        Assert.Empty(fakes.Logs.Calls);
        Assert.Empty(fakes.Traces.Calls);
    }
}
