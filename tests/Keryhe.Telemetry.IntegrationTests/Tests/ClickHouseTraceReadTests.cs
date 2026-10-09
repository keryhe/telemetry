using Keryhe.Telemetry.ClickHouse.Services;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// ClickHouse-only trace reads on top of the shared <c>TracePhase3TestsBase</c> (plans/clickhouse-redesign phase 4): a list across
/// midnight against a LINQ restatement, search terms, the dashboard samples, export, detail fields, span lookups and the
/// analytics reads.
/// </summary>
[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseTraceReadTests(ClickHouseFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime Midnight = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    private static string TraceId(int n) => n.ToString("x32");
    private static string SpanId(int n) => n.ToString("x16");

    private SpanModel Span(int trace, int span, string service, string name, SpanKind kind, DateTime start, double ms,
        int? parent = null, SpanStatusCode status = SpanStatusCode.OK, Dictionary<string, object>? attributes = null, Dictionary<string, object>? resourceExtra = null) => new()
    {
        TraceIdHex = TraceId(trace), SpanIdHex = SpanId(span), ParentSpanIdHex = parent is null ? null : SpanId(parent.Value),
        Name = name, Kind = kind, StatusCode = status, Attributes = attributes,
        StartTimeUnixNano = SeededDataBuilder.ToUnixNano(start), EndTimeUnixNano = SeededDataBuilder.ToUnixNano(start) + (long)(ms * 1_000_000),
        Resource = SeededDataBuilder.Resource(fixture.TenantId, service, extra: resourceExtra), InstrumentationScope = SeededDataBuilder.Scope()
    };

    private async Task FlushAsync(IEnumerable<SpanModel> spans)
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans.ToList());
    }

    private ITraceReadRepository Repo(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ITraceReadRepository>();

    private async Task<TraceListResult> ListAsync(DateTime start, DateTime end, string? service = null, string? search = null, string mode = "all", string order = ListOrder.Newest, int limit = 500)
    {
        using var scope = fixture.Services.CreateScope();
        return await Repo(scope).GetTraceListAsync(new TraceQuery { Start = start, End = end, Service = service, Search = search, Mode = mode, Order = order, Limit = limit, MinDurationMs = mode == "slow" ? 500 : null });
    }

    [Fact]
    public async Task AListAcrossMidnight_ReturnsEachTraceOnce_AnchoredOnItsEarliestSpan_InBothOrders()
    {
        var spans = new List<SpanModel>();
        for (var t = 1; t <= 30; t++)
        {
            // starts spread over two hours either side of midnight; some traces cross it
            var start = Midnight.AddMinutes(-120 + t * 8);
            spans.Add(Span(t, t * 10, "svc-a", $"op-{t % 3}", SpanKind.SERVER, start, 50));
            spans.Add(Span(t, t * 10 + 1, "svc-b", "child", SpanKind.CLIENT, start.AddMinutes(t % 2 == 0 ? 30 : 0).AddMilliseconds(5), 20, parent: t * 10));
        }
        await FlushAsync(spans);

        foreach (var order in new[] { ListOrder.Newest, ListOrder.Oldest })
        {
            var result = await ListAsync(Midnight.AddHours(-3), Midnight.AddHours(3), order: order);
            Assert.Equal(30, result.Items.Count);
            Assert.False(result.Truncated);
            Assert.All(result.Items, i => Assert.Equal(2, i.SpanCount));
            Assert.All(result.Items, i => Assert.Equal("svc-a", i.ServiceName));
            var starts = result.Items.Select(i => i.TraceStartTime).ToList();
            Assert.Equal(ListOrder.IsOldest(order) ? starts.Order().ToList() : starts.OrderDescending().ToList(), starts);
        }

        // a trace crossing midnight (t=15: starts at -120+120 = 0, i.e. exactly midnight) is listed under the day it started on
        var window = await ListAsync(Midnight.AddMinutes(-30), Midnight.AddMinutes(30));
        var expected = spans.Where(s => s.ParentSpanIdHex == null).Count(s =>
            s.StartTimeUnixNano >= SeededDataBuilder.ToUnixNano(Midnight.AddMinutes(-30)) && s.StartTimeUnixNano <= SeededDataBuilder.ToUnixNano(Midnight.AddMinutes(30)));
        Assert.Equal(expected, window.Items.Count);
    }

    [Fact]
    public async Task SearchTerms_AttributeResourceNegationAndTraceId()
    {
        await FlushAsync(
        [
            Span(1, 1, "svc-a", "alpha", SpanKind.SERVER, Midnight, 10, attributes: new() { ["http.route"] = "/pay", ["retry"] = true }, resourceExtra: new() { ["k8s.pod"] = "pod-7" }),
            Span(2, 2, "svc-a", "beta", SpanKind.SERVER, Midnight.AddSeconds(1), 10, attributes: new() { ["http.route"] = "/cart", ["status"] = 500L }),
            Span(3, 3, "svc-a", "gamma", SpanKind.SERVER, Midnight.AddSeconds(2), 10),
        ]);
        var end = Midnight.AddMinutes(1);
        async Task<string[]> Ids(string search) => (await ListAsync(Midnight.AddMinutes(-1), end, search: search)).Items.Select(i => i.TraceIdHex).Order().ToArray();

        Assert.Equal([TraceId(1)], await Ids("http.route:/pay"));
        Assert.Equal([TraceId(1)], await Ids("http.route=/PAY"));            // case-insensitive
        Assert.Equal([TraceId(2)], await Ids("status:500"));
        Assert.Equal([TraceId(1)], await Ids("retry:true"));
        Assert.Equal([TraceId(1)], await Ids("k8s.pod:pod-7"));              // a resource attribute
        Assert.Equal([TraceId(1), TraceId(3)], await Ids("-http.route:/cart"));
        Assert.Equal([TraceId(2)], await Ids("BET"));                         // free text in the name, any case
        Assert.Equal([TraceId(2)], await Ids("http.route:/cart AND status:500"));
        Assert.Empty(await Ids("http.route:/pay AND status:500"));
        Assert.Equal([TraceId(3)], await Ids(TraceId(3)));                    // a trace id
    }

    [Fact]
    public async Task Samples_Errors_AreTheNewestErrorTraces_AndSlowest_AreTheLongestAnchors()
    {
        var spans = new List<SpanModel>();
        for (var t = 1; t <= 8; t++)
        {
            spans.Add(Span(t, t, "svc-a", $"op-{t}", SpanKind.SERVER, Midnight.AddSeconds(t), t * 200, status: t % 2 == 0 ? SpanStatusCode.ERROR : SpanStatusCode.OK));
        }
        // a long span that is NOT its trace's anchor must not count as a slow anchor
        spans.Add(Span(9, 90, "svc-a", "short-root", SpanKind.SERVER, Midnight.AddSeconds(20), 5));
        spans.Add(Span(9, 91, "svc-a", "long-child", SpanKind.CLIENT, Midnight.AddSeconds(20).AddMilliseconds(1), 9000, parent: 90));
        await FlushAsync(spans);

        using var scope = fixture.Services.CreateScope();
        var repo = Repo(scope);
        var errors = await repo.GetTraceSamplesAsync(new TraceSamplesQuery { Start = Midnight.AddMinutes(-1), End = Midnight.AddMinutes(5), Kind = "errors", Limit = 3 });
        Assert.False(errors.TimedOut);
        Assert.Equal([TraceId(8), TraceId(6), TraceId(4)], errors.Items.Select(i => i.TraceIdHex).ToArray());
        Assert.All(errors.Items, i => Assert.True(i.HasErrors));

        var slowest = await repo.GetTraceSamplesAsync(new TraceSamplesQuery { Start = Midnight.AddMinutes(-1), End = Midnight.AddMinutes(5), Kind = "slowest", Limit = 3 });
        Assert.Equal([TraceId(8), TraceId(7), TraceId(6)], slowest.Items.Select(i => i.TraceIdHex).ToArray()); // 1600, 1400, 1200 ms (> 500 ms floor)
        Assert.DoesNotContain(slowest.Items, i => i.TraceIdHex == TraceId(9));
        Assert.Equal(TimeSpan.FromMilliseconds(1600), slowest.Items[0].TraceDuration);
    }

    [Fact]
    public async Task Export_StreamsTheSameTracesAsTheList_OldestFirst_AndAcrossChunks()
    {
        var spans = new List<SpanModel>();
        for (var t = 1; t <= 60; t++)
        {
            spans.Add(Span(t, t * 10, t % 2 == 0 ? "svc-a" : "svc-b", "root", SpanKind.SERVER, Midnight.AddSeconds(t), 20 + t, status: t % 7 == 0 ? SpanStatusCode.ERROR : SpanStatusCode.OK));
            spans.Add(Span(t, t * 10 + 1, "svc-c", "leaf", SpanKind.CLIENT, Midnight.AddSeconds(t).AddMilliseconds(3), 5, parent: t * 10));
        }
        await FlushAsync(spans);

        foreach (var (mode, service) in new[] { ("all", (string?)null), ("errors", null), ("all", "svc-a"), ("slow", null) })
        {
            var list = await ListAsync(Midnight.AddMinutes(-1), Midnight.AddMinutes(5), service: service, mode: mode, order: ListOrder.Oldest);
            using var scope = fixture.Services.CreateScope();
            var exported = new List<TraceInfo>();
            await foreach (var info in Repo(scope).ExportTracesAsync(new TraceExportQuery
            {
                Start = Midnight.AddMinutes(-1), End = Midnight.AddMinutes(5), Mode = mode, Service = service, MinDurationMs = mode == "slow" ? 50 : null
            }))
                exported.Add(info);

            if (mode == "slow") { Assert.NotEmpty(exported); continue; } // different floor than the list's 500 ms; just exercised
            Assert.Equal(list.Items.Select(i => i.TraceIdHex), exported.Select(i => i.TraceIdHex));
            Assert.Equal(list.Items.Select(i => (i.SpanCount, i.HasErrors, i.DisplaySpanIdHex)), exported.Select(i => (i.SpanCount, i.HasErrors, i.DisplaySpanIdHex)));
        }
    }

    [Fact]
    public async Task Detail_RoundTripsEventsLinksAndAttributes_AndSpanLookupsWork()
    {
        var root = Span(1, 1, "svc-a", "root", SpanKind.SERVER, Midnight, 100, attributes: new() { ["n"] = 5L });
        root.Events = [new SpanEventModel { Name = "ev", TimeUnixNano = SeededDataBuilder.ToUnixNano(Midnight.AddMilliseconds(5)), DroppedAttributesCount = 2, Attributes = new() { ["ea"] = "eb" } }];
        root.Links = [new SpanLinkModel { LinkedTraceIdHex = TraceId(77), LinkedSpanIdHex = SpanId(78), TraceState = "ts", Flags = 1, Attributes = new() { ["la"] = "lb" } }];
        root.TraceState = "a=b"; root.StatusMessage = "boom"; root.StatusCode = SpanStatusCode.ERROR;
        var child = Span(1, 2, "svc-b", "child", SpanKind.CLIENT, Midnight.AddMilliseconds(10), 20, parent: 1);
        await FlushAsync([root, child]);

        using var scope = fixture.Services.CreateScope();
        var repo = Repo(scope);
        var detail = await repo.GetTraceByIdAsync(TraceId(1));
        Assert.Equal(2, detail.Count);
        var r = detail.Single(s => s.SpanIdHex == SpanId(1));
        Assert.Null(r.ParentSpanIdHex);
        Assert.Equal("5", r.Attributes!["n"]);
        Assert.Equal(("a=b", "boom", SpanStatusCode.ERROR), (r.TraceState, r.StatusMessage, r.StatusCode));
        var ev = Assert.Single(r.Events);
        Assert.Equal(("ev", 2, "eb"), (ev.Name, ev.DroppedAttributesCount, (string)ev.Attributes!["ea"]));
        Assert.Equal(root.Events[0].TimeUnixNano, ev.TimeUnixNano);
        var link = Assert.Single(r.Links);
        Assert.Equal((TraceId(77), SpanId(78), "ts", 1), (link.LinkedTraceIdHex, link.LinkedSpanIdHex, link.TraceState, link.Flags));
        Assert.Equal(root.EndTimeUnixNano - root.StartTimeUnixNano, r.EndTimeUnixNano - r.StartTimeUnixNano);
        Assert.NotSame(r.Resource, detail.Single(s => s.SpanIdHex == SpanId(2)).Resource); // different services, different resources

        Assert.Equal("child", (await repo.GetSpanByIdAsync(TraceId(1), SpanId(2)))!.Name);
        Assert.Null(await repo.GetSpanByIdAsync(TraceId(1), SpanId(99)));
        Assert.Equal([SpanId(2)], (await repo.GetSpansByParentAsync(TraceId(1), SpanId(1))).Select(s => s.SpanIdHex).ToArray());
        await Assert.ThrowsAsync<ArgumentException>(() => repo.GetTraceByIdAsync("not-hex"));
    }

    [Fact]
    public async Task Analytics_Dependencies_OperationStats_Counts_Latencies_AndTheSlowRequestCount()
    {
        var spans = new List<SpanModel>();
        for (var t = 1; t <= 10; t++)
        {
            spans.Add(Span(t, t * 10, "svc-gw", "GET /x", SpanKind.SERVER, Midnight.AddSeconds(t), 100 * t, status: t <= 2 ? SpanStatusCode.ERROR : SpanStatusCode.OK));
            spans.Add(Span(t, t * 10 + 1, "svc-db", "query", SpanKind.CLIENT, Midnight.AddSeconds(t).AddMilliseconds(1), 10, parent: t * 10, status: t == 1 ? SpanStatusCode.ERROR : SpanStatusCode.OK));
        }
        await FlushAsync(spans);
        var from = Midnight.AddMinutes(-1);
        var to = Midnight.AddMinutes(5);

        using var scope = fixture.Services.CreateScope();
        var repo = Repo(scope);

        var deps = await repo.GetServiceDependenciesAsync(from, to);
        var dep = Assert.Single(deps);
        Assert.Equal(("svc-gw", "svc-db", SpanKind.CLIENT, 10, 1), (dep.ParentService, dep.ChildService, dep.SpanKind, dep.CallCount, dep.ErrorCount));
        Assert.Equal(10, dep.AvgDurationMs, 0.5);

        Assert.Equal(10, (await repo.GetOperationCountsAsync("svc-gw", from, to))["GET /x"]);
        Assert.Empty(await repo.GetOperationCountsAsync("svc-db", from, to)); // only inbound requests are rolled up
        var stats = Assert.Single(await repo.GetOperationStatsAsync("svc-gw", from, to));
        Assert.Equal(("GET /x", 10, 2), (stats.Operation, stats.Count, stats.ErrorCount));
        Assert.Equal(20, stats.ErrorRate, 3);
        Assert.Equal(550, stats.AvgMs, 1);
        Assert.InRange(stats.P95Ms, 400, 1100); // approximate: the doubling bands
        Assert.Equal(550, (await repo.GetAverageLatenciesAsync("svc-gw", from, to))["GET /x"], 1);

        Assert.Equal(6, (await repo.CountSlowInboundSpansAsync(from, to, null, 500)).Count);   // 500..1000 ms
        Assert.Equal(0, (await repo.CountSlowInboundSpansAsync(from, to, "svc-db", 1)).Count); // CLIENT spans are not inbound
    }
}
