using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Trace-list correctness checks for the schema-simplification plan's anchor semantics (decisions
/// 7, 10-12 and 14-18): each trace is anchored on its EARLIEST span in scope (the selected service's
/// own earliest span when a service is selected); duration, error flag and span count are scoped to
/// the row; the operation filter matches the anchor's name; search matches any span in the whole
/// trace; and a re-delivered span batch is stored twice but counted and shown once. Also the capped
/// list's order, limit and truncation (plans/list-caps.md).
/// </summary>
public abstract class TracePhase3TestsBase : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    protected TracePhase3TestsBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    private async Task FlushAsync(params SpanModel[] spans) => await FlushAsync((IEnumerable<SpanModel>)spans);

    private async Task FlushAsync(IEnumerable<SpanModel> spans)
    {
        using var scope = Scope();
        await scope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans.ToList());
    }

    private static string NewTraceId() => Guid.NewGuid().ToString("N");
    private static string NewSpanId() => Guid.NewGuid().ToString("N")[..16];

    private SpanModel Span(
        string traceId, string service, string name, SpanKind kind, DateTime start, double durationMs,
        string? parent = null, SpanStatusCode status = SpanStatusCode.OK, string? spanId = null)
        => new()
        {
            TraceIdHex = traceId,
            SpanIdHex = spanId ?? NewSpanId(),
            ParentSpanIdHex = parent,
            Name = name,
            Kind = kind,
            StartTimeUnixNano = SeededDataBuilder.ToUnixNano(start),
            EndTimeUnixNano = SeededDataBuilder.ToUnixNano(start) + (long)(durationMs * 1_000_000),
            StatusCode = status,
            Resource = SeededDataBuilder.Resource(_fixture.TenantId, service),
            InstrumentationScope = SeededDataBuilder.Scope()
        };

    private async Task<TraceListResult> ListAsync(
        string? service = null, string? operation = null, string? search = null, string mode = "all",
        DateTime? start = null, DateTime? end = null, double? minDurationMs = null, string order = ListOrder.Newest, int limit = 500)
    {
        using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<ITraceReadRepository>().GetTraceListAsync(new TraceQuery
        {
            Start = start ?? WindowStart.AddMinutes(-1),
            End = end ?? WindowStart.AddHours(1),
            Service = service, Operation = operation, Search = search, Mode = mode, Limit = limit, Order = order,
            MinDurationMs = minDurationMs
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Anchors
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnscopedAnchor_IsTheEarliestSpanOfEachTrace_WithOrWithoutARoot()
    {
        var withRoot = NewTraceId();
        var rootId = NewSpanId();
        var rootless = NewTraceId();

        await FlushAsync(
            // The child is written FIRST and starts later; the root is the earliest span.
            Span(withRoot, "svc-a", "SELECT x", SpanKind.CLIENT, WindowStart.AddMilliseconds(10), 40, parent: rootId),
            Span(withRoot, "svc-a", "POST /pay", SpanKind.SERVER, WindowStart, 120, spanId: rootId),
            // No span of this trace has a null parent: its earliest span is its anchor anyway.
            Span(rootless, "svc-a", "rootless-late", SpanKind.CLIENT, WindowStart.AddSeconds(3), 20, parent: NewSpanId()),
            Span(rootless, "svc-a", "rootless-early", SpanKind.SERVER, WindowStart.AddSeconds(1), 200, parent: NewSpanId()));

        var page = await ListAsync();

        Assert.Equal(2, page.Items.Count);
        var a = Assert.Single(page.Items, t => t.TraceIdHex == withRoot);
        Assert.Equal("POST /pay", a.RootOperationName);
        Assert.Equal(rootId, a.DisplaySpanIdHex);
        Assert.Equal("SERVER", a.AnchorKind);
        Assert.Equal(TimeSpan.FromMilliseconds(120), a.TraceDuration);
        var b = Assert.Single(page.Items, t => t.TraceIdHex == rootless);
        Assert.Equal("rootless-early", b.RootOperationName);
        Assert.Equal(TimeSpan.FromMilliseconds(200), b.TraceDuration);
    }

    [Fact]
    public async Task RootArrivingLater_BecomesTheAnchorOnTheNextQuery()
    {
        var traceId = NewTraceId();
        await FlushAsync(Span(traceId, "svc-a", "late-span", SpanKind.SERVER, WindowStart.AddSeconds(5), 30, parent: NewSpanId()));

        var before = await ListAsync();
        Assert.Equal("late-span", Assert.Single(before.Items).RootOperationName);

        // The true root (earlier start) arrives after the first read.
        await FlushAsync(Span(traceId, "svc-a", "the-root", SpanKind.SERVER, WindowStart, 500));

        var after = await ListAsync();
        var item = Assert.Single(after.Items);
        Assert.Equal("the-root", item.RootOperationName);
        Assert.Equal(TimeSpan.FromMilliseconds(500), item.TraceDuration);
    }

    [Fact]
    public async Task ServiceScopedAnchor_IsThatServicesEarliestSpan_AndAServiceThatNeverRootsATraceStillListsIt()
    {
        var traceId = NewTraceId();
        var gatewayId = NewSpanId();
        await FlushAsync(
            Span(traceId, "svc-gateway", "GET /api", SpanKind.SERVER, WindowStart, 300, spanId: gatewayId),
            Span(traceId, "svc-backend", "handle", SpanKind.SERVER, WindowStart.AddMilliseconds(50), 100, parent: gatewayId),
            Span(traceId, "svc-backend", "backend-work", SpanKind.INTERNAL, WindowStart.AddMilliseconds(70), 20),
            Span(traceId, "svc-db", "SELECT", SpanKind.CLIENT, WindowStart.AddMilliseconds(80), 10, status: SpanStatusCode.ERROR));

        var backend = Assert.Single((await ListAsync(service: "svc-backend")).Items);
        Assert.Equal(traceId, backend.TraceIdHex);
        Assert.Equal("svc-backend", backend.ServiceName);
        Assert.Equal("handle", backend.RootOperationName);
        Assert.Equal(TimeSpan.FromMilliseconds(100), backend.TraceDuration);
        Assert.Equal(2, backend.SpanCount);          // the service's own spans, not the whole trace's four
        Assert.False(backend.HasErrors);              // the error span belongs to another service

        var db = Assert.Single((await ListAsync(service: "svc-db")).Items);
        Assert.Equal("CLIENT", db.AnchorKind);
        Assert.True(db.HasErrors);
        Assert.Equal(1, db.SpanCount);

        var whole = Assert.Single((await ListAsync()).Items);
        Assert.Equal("svc-gateway", whole.ServiceName);
        Assert.Equal(4, whole.SpanCount);
        Assert.True(whole.HasErrors);
    }

    [Fact]
    public async Task SlowFilter_AndRowDuration_UseTheAnchorsOwnDuration()
    {
        var slow = NewTraceId();
        var fast = NewTraceId();
        await FlushAsync(
            Span(slow, "svc-a", "slow-op", SpanKind.SERVER, WindowStart, 1234),
            // The fast trace's anchor is short even though a late child makes the whole trace long.
            Span(fast, "svc-a", "fast-op", SpanKind.SERVER, WindowStart.AddSeconds(1), 20),
            Span(fast, "svc-a", "long-tail", SpanKind.CLIENT, WindowStart.AddSeconds(1).AddMilliseconds(5), 5000));

        var page = await ListAsync(mode: "slow", minDurationMs: 1000);
        var row = Assert.Single(page.Items);
        Assert.Equal(slow, row.TraceIdHex);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), row.TraceDuration);

        // Unfiltered, both anchors are listed (the fast trace's anchor is 20 ms even though a late child makes the whole trace long).
        var all = await ListAsync();
        Assert.Equal(2, all.Items.Count);
        Assert.Equal(new[] { 20.0, 1234.0 }, all.Items.Select(i => i.TraceDuration.TotalMilliseconds).Order().ToArray());
    }

    [Fact]
    public async Task OperationFilter_MatchesTheAnchorsNameOnly()
    {
        var traceId = NewTraceId();
        var rootId = NewSpanId();
        await FlushAsync(
            Span(traceId, "svc-a", "POST /pay", SpanKind.SERVER, WindowStart, 200, spanId: rootId),
            Span(traceId, "svc-a", "GET /cart", SpanKind.CLIENT, WindowStart.AddMilliseconds(20), 50, parent: rootId));

        Assert.Empty((await ListAsync(operation: "GET /cart")).Items);
        Assert.Equal(traceId, Assert.Single((await ListAsync(operation: "POST /pay")).Items).TraceIdHex);
    }

    [Fact]
    public async Task ErrorsMode_UsesTheScopedErrorFlag()
    {
        var okTrace = NewTraceId();
        var errTrace = NewTraceId();
        await FlushAsync(
            Span(okTrace, "svc-a", "ok-op", SpanKind.SERVER, WindowStart, 10),
            Span(errTrace, "svc-a", "err-op", SpanKind.SERVER, WindowStart.AddSeconds(1), 10),
            Span(errTrace, "svc-b", "boom", SpanKind.CLIENT, WindowStart.AddSeconds(1).AddMilliseconds(2), 5, status: SpanStatusCode.ERROR));

        var unscoped = await ListAsync(mode: "errors");
        Assert.Equal(errTrace, Assert.Single(unscoped.Items).TraceIdHex);

        // Scoped to svc-a the error span (svc-b's) is out of scope, so the trace is not an error row.
        Assert.Empty((await ListAsync(service: "svc-a", mode: "errors")).Items);
        Assert.Equal(errTrace, Assert.Single((await ListAsync(service: "svc-b", mode: "errors")).Items).TraceIdHex);
    }

    [Fact]
    public async Task Search_WithAServiceSelected_FindsATraceWhoseMatchingSpanBelongsToAnotherService()
    {
        var traceId = NewTraceId();
        var gatewayId = NewSpanId();
        await FlushAsync(
            Span(traceId, "svc-gateway", "GET /api", SpanKind.SERVER, WindowStart, 300, spanId: gatewayId),
            Span(traceId, "svc-backend", "rare-needle-span", SpanKind.SERVER, WindowStart.AddMilliseconds(50), 100, parent: gatewayId));

        var page = await ListAsync(service: "svc-gateway", search: "rare-needle");
        var item = Assert.Single(page.Items);
        Assert.Equal(traceId, item.TraceIdHex);
        // Still anchored on the SELECTED service's earliest span, not on the span that matched.
        Assert.Equal("svc-gateway", item.ServiceName);
        Assert.Equal("GET /api", item.RootOperationName);

        Assert.Empty((await ListAsync(service: "svc-gateway", search: "no-such-text-anywhere")).Items);
    }

    [Fact]
    public async Task LookbackMargin_ExcludesATraceThatStartedBeforeTheWindow_AndIncludesOneThatStartedInside()
    {
        var windowStart = WindowStart.AddMinutes(10);
        var windowEnd = WindowStart.AddMinutes(20);

        var early = NewTraceId();
        var inside = NewTraceId();
        await FlushAsync(
            // Began 2 minutes before the window (inside the 5-minute look-back) and carried on into it.
            Span(early, "svc-a", "began-before", SpanKind.SERVER, windowStart.AddMinutes(-2), 200),
            Span(early, "svc-a", "continues-inside", SpanKind.CLIENT, windowStart.AddMinutes(1), 20),
            Span(inside, "svc-a", "began-inside", SpanKind.SERVER, windowStart.AddMinutes(1), 50));

        var page = await ListAsync(start: windowStart, end: windowEnd);
        Assert.Equal(inside, Assert.Single(page.Items).TraceIdHex);
    }

    [Fact]
    public async Task TheList_ShowsEveryKindOfAnchor()
    {
        var kinds = new[] { SpanKind.SERVER, SpanKind.CONSUMER, SpanKind.CLIENT, SpanKind.INTERNAL };
        await FlushAsync(kinds.Select((kind, i) =>
            Span(NewTraceId(), "svc-a", $"op-{kind}", kind, WindowStart.AddSeconds(i), 30)));

        var page = await ListAsync();
        Assert.Equal(4, page.Items.Count);
        Assert.Equal(kinds.Select(k => k.ToString()).Order(), page.Items.Select(t => t.AnchorKind!).Order());
    }

    // ---------------------------------------------------------------------------------------------
    // Sliced reads (trace-list-detail-performance plan, Phase 4): the list is read from slices of the
    // window instead of ranking all of it, and must agree with the anchor DEFINITION for every
    // filter, order, limit and slice width. The expected rows are computed here in LINQ from the seeded
    // spans, independently of any SQL.
    // ---------------------------------------------------------------------------------------------

    private sealed record ExpectedTrace(string TraceId, string AnchorSpanId, long AnchorStart, string Service, string Name, long DurationNs, bool HasError, int SpanCount);

    /// <summary>The anchor definition (CLAUDE.md, "Trace anchors"), restated over in-memory spans.</summary>
    private List<ExpectedTrace> ExpectedAnchors(
        IReadOnlyList<SpanModel> spans, DateTime windowStart, DateTime windowEnd,
        string? service = null, string? operation = null, string mode = "all", double minDurationMs = 500, string? needle = null)
    {
        var startNs = SeededDataBuilder.ToUnixNano(windowStart);
        var endNs = SeededDataBuilder.ToUnixNano(windowEnd);
        var fromNs = startNs - 5 * 60_000_000_000L;
        string ServiceOf(SpanModel s) => (string)s.Resource.Attributes["service.name"];

        var result = new List<ExpectedTrace>();
        foreach (var trace in spans.GroupBy(s => s.TraceIdHex))
        {
            var inScope = trace.Where(s => s.StartTimeUnixNano >= fromNs && s.StartTimeUnixNano <= endNs
                                           && (service == null || ServiceOf(s) == service)).ToList();
            if (inScope.Count == 0) continue;
            var anchor = inScope.OrderBy(s => s.StartTimeUnixNano).First();
            if (anchor.StartTimeUnixNano < startNs) continue;
            var duration = anchor.EndTimeUnixNano - anchor.StartTimeUnixNano;
            var hasError = inScope.Any(s => s.StatusCode == SpanStatusCode.ERROR);
            if (operation != null && anchor.Name != operation) continue;
            if (mode == "errors" && !hasError) continue;
            if (mode == "slow" && duration < (long)(minDurationMs * 1_000_000)) continue;
            if (needle != null && !trace.Any(s => s.StartTimeUnixNano >= fromNs && s.StartTimeUnixNano <= endNs
                                                  && s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))) continue;
            var spanCount = trace.Where(s => service == null || ServiceOf(s) == service).Select(s => s.SpanIdHex).Distinct().Count();
            result.Add(new ExpectedTrace(trace.Key, anchor.SpanIdHex, anchor.StartTimeUnixNano, ServiceOf(anchor), anchor.Name, duration, hasError, spanCount));
        }
        return result;
    }

    [Fact]
    public async Task SlicedList_MatchesTheAnchorDefinition_ForEveryFilterOrderLimitAndSliceWidth()
    {
        var rng = new Random(7);
        var windowStart = WindowStart;
        var windowEnd = WindowStart.AddMinutes(10);
        var services = new[] { "svc-a", "svc-b", "svc-c" };
        var spans = new List<SpanModel>();
        for (var t = 0; t < 90; t++)
        {
            var traceId = NewTraceId();
            // Starts range from before the look-back margin (excluded) through the margin (excluded, anchored earlier) to the
            // window's end; every ninth trace starts on a whole second shared with others (ties across traces).
            var traceStart = windowStart.AddSeconds(-720 + rng.NextDouble() * 1320);
            if (t % 9 == 0) traceStart = windowStart.AddSeconds(Math.Floor(rng.NextDouble() * 600));
            var spanCount = 1 + rng.Next(5);
            SpanModel? first = null;
            for (var k = 0; k < spanCount; k++)
            {
                var svc = services[rng.Next(services.Length)];
                var offset = k == 0 ? 0 : rng.NextDouble() * 20;
                var duration = k == 0 && t % 5 == 0 ? 800 : 10 + rng.Next(300);
                var name = k == 2 && t % 4 == 0 ? $"needle-{k}" : $"op-{svc}-{rng.Next(3)}";
                var status = rng.NextDouble() < 0.12 ? SpanStatusCode.ERROR : SpanStatusCode.OK;
                var kind = k == 0 ? SpanKind.SERVER : SpanKind.CLIENT;
                // A tie with the trace's first span: identical in everything observable, so which row is the anchor does not matter.
                if (t % 7 == 0 && k == 1 && first is not null)
                {
                    svc = (string)first.Resource.Attributes["service.name"];
                    offset = 0; duration = (int)((first.EndTimeUnixNano - first.StartTimeUnixNano) / 1_000_000);
                    name = first.Name; status = first.StatusCode; kind = first.Kind;
                }
                var span = Span(traceId, svc, name, kind, traceStart.AddSeconds(offset), duration, parent: k == 0 ? null : first!.SpanIdHex, status: status);
                first ??= span;
                spans.Add(span);
            }
        }
        await FlushAsync(spans);
        var spansById = spans.ToDictionary(s => s.SpanIdHex);

        var filters = new (string Name, string? Service, string? Operation, string Mode, string? Search)[]
        {
            ("all", null, null, "all", null),
            ("service", "svc-b", null, "all", null),
            ("operation", "svc-a", "op-svc-a-1", "all", null),
            ("slow", null, null, "slow", null),
            ("slow+service", "svc-c", null, "slow", null),
            ("errors", null, null, "errors", null),
            ("errors+service", "svc-a", null, "errors", null),
            ("search", null, null, "all", "needle"),
        };

        foreach (var (sliceSeconds, growth) in new[] { (2, 4), (1, 2) })
        {
            using var provider = _fixture.CreateServices(new Dictionary<string, string?>
            {
                ["Telemetry:Query:PageSliceSeconds"] = sliceSeconds.ToString(),
                ["Telemetry:Query:PageSliceGrowth"] = growth.ToString(),
            });
            using var scope = provider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ITraceReadRepository>();

            foreach (var f in filters)
            {
                var expected = ExpectedAnchors(spans, windowStart, windowEnd, f.Service, f.Operation, f.Mode, 500, f.Search)
                    .ToDictionary(e => e.TraceId);
                Assert.NotEmpty(expected); // a vacuous comparison would prove nothing

                foreach (var order in new[] { ListOrder.Newest, ListOrder.Oldest })
                foreach (var limit in new[] { 7, 50, expected.Count, 1000 })
                {
                    var ctx = $"slice={sliceSeconds}s x{growth}, filter={f.Name}, order={order}, limit={limit}";
                    var result = await repo.GetTraceListAsync(new TraceQuery
                    {
                        Start = windowStart, End = windowEnd, Service = f.Service, Operation = f.Operation, Mode = f.Mode, Search = f.Search,
                        MinDurationMs = f.Mode == "slow" ? 500 : null, Limit = limit, Order = order,
                    });
                    var items = result.Items;

                    // The list is the first `limit` anchors from the requested end of the window. Traces that tie on the anchor's
                    // start can be cut either way at the boundary, so the order is checked on the start times, and every row
                    // is checked against its own expected anchor.
                    var oldest = ListOrder.IsOldest(order);
                    var expectedStarts = (oldest ? expected.Values.OrderBy(e => e.AnchorStart) : expected.Values.OrderByDescending(e => e.AnchorStart))
                        .Take(limit).Select(e => e.AnchorStart).ToList();
                    var actualStarts = items.Select(i => spansById[i.DisplaySpanIdHex!].StartTimeUnixNano).ToList();
                    Assert.True(expectedStarts.SequenceEqual(actualStarts), $"{ctx}: the list is not the {order} {limit} anchors");
                    Assert.True(result.Truncated == expected.Count > limit, $"{ctx}: truncated was {result.Truncated} with {expected.Count} matching");
                    Assert.Equal(items.Count, items.Select(i => i.TraceIdHex).Distinct().Count()); // one row per trace

                    foreach (var item in items)
                    {
                        var e = expected[item.TraceIdHex];
                        var anchorStart = spansById[item.DisplaySpanIdHex!].StartTimeUnixNano;
                        Assert.True(e.AnchorStart == anchorStart, $"{ctx}: trace {item.TraceIdHex} anchored on a span starting at {anchorStart}, expected {e.AnchorStart}");
                        Assert.Equal(e.Service, item.ServiceName);
                        Assert.Equal(e.Name, item.RootOperationName);
                        Assert.Equal(TimeSpan.FromTicks(e.DurationNs / 100), item.TraceDuration);
                        Assert.True(e.HasError == item.HasErrors, $"{ctx}: trace {item.TraceIdHex} error flag");
                        Assert.True(e.SpanCount == item.SpanCount, $"{ctx}: trace {item.TraceIdHex} span count {item.SpanCount}, expected {e.SpanCount}");
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Trace detail (trace-list-detail-performance plan, Phase 6): the start-time hint and the shared resources/scopes.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Whether this provider reads only the range of a trace-time hint (ClickHouse's partitions); the others ignore it.</summary>
    protected virtual bool HonorsStartHint => false;

    private async Task<List<SpanModel>> DetailAsync(string traceId, TraceTimeHint? hint, IReadOnlyDictionary<string, string?>? config = null)
    {
        if (config is null)
        {
            using var scope = Scope();
            return await scope.ServiceProvider.GetRequiredService<ITraceReadRepository>().GetTraceByIdAsync(traceId, hint);
        }
        using var provider = _fixture.CreateServices(config);
        using var configured = provider.CreateScope();
        return await configured.ServiceProvider.GetRequiredService<ITraceReadRepository>().GetTraceByIdAsync(traceId, hint);
    }

    [Fact]
    public async Task DetailHint_ReturnsTheSameSpansAsTheUnhintedRead()
    {
        var traceId = NewTraceId();
        var rootId = NewSpanId();
        await FlushAsync(
            Span(traceId, "svc-a", "root", SpanKind.SERVER, WindowStart, 400, spanId: rootId),
            Span(traceId, "svc-b", "child-1", SpanKind.CLIENT, WindowStart.AddMilliseconds(20), 100, parent: rootId),
            Span(traceId, "svc-b", "child-2", SpanKind.SERVER, WindowStart.AddMilliseconds(30), 50, parent: rootId),
            Span(traceId, "svc-a", "late-child", SpanKind.INTERNAL, WindowStart.AddSeconds(90), 5, parent: rootId));

        var unhinted = await DetailAsync(traceId, null);
        // The extent the trace list returns: the earliest start and the latest end.
        var hinted = await DetailAsync(traceId, new TraceTimeHint(WindowStart, WindowStart.AddSeconds(90).AddMilliseconds(5)));

        Assert.Equal(4, unhinted.Count);
        Assert.Equal(unhinted.Select(x => x.SpanIdHex), hinted.Select(x => x.SpanIdHex));
    }

    [Fact]
    public async Task DetailHint_ThatMissesTheTrace_FallsBackToTheUnboundedRead()
    {
        var traceId = NewTraceId();
        await FlushAsync(Span(traceId, "svc-a", "root", SpanKind.SERVER, WindowStart, 400));

        // Three days off: any bounded read around it finds nothing, and the trace must still come back.
        var spans = await DetailAsync(traceId, new TraceTimeHint(WindowStart.AddDays(3), WindowStart.AddDays(3).AddSeconds(1)));
        Assert.Equal("root", Assert.Single(spans).Name);
    }

    [Fact]
    public async Task DetailHint_BoundsTheRead_WhereAProviderHonoursIt_SoASpanFarBeyondTheHintedExtentIsNotReturned()
    {
        var traceId = NewTraceId();
        var rootId = NewSpanId();
        await FlushAsync(
            Span(traceId, "svc-a", "root", SpanKind.SERVER, WindowStart, 400, spanId: rootId),
            Span(traceId, "svc-a", "inside-the-margin", SpanKind.INTERNAL, WindowStart.AddSeconds(30), 5, parent: rootId),
            Span(traceId, "svc-a", "two-hours-later", SpanKind.INTERNAL, WindowStart.AddHours(2), 5, parent: rootId));
        // The extent the list had when it was read: the root alone, 400 ms.
        var hint = new TraceTimeHint(WindowStart, WindowStart.AddMilliseconds(400));

        // Without a hint the trace is whole on every provider.
        Assert.Equal(3, (await DetailAsync(traceId, null)).Count);

        // With the hint, a bounded provider reads [start - 1 min, end + 1 min]: the span 30 s after the end is inside the margin and
        // comes back, the one two hours later is the documented limit of a hint (a span that arrived after the list was read). The
        // other providers ignore the hint and return all three: that is the negative control.
        var hinted = await DetailAsync(traceId, hint);
        Assert.Equal(HonorsStartHint ? 2 : 3, hinted.Count);
        Assert.Contains(hinted, x => x.Name == "inside-the-margin");
    }

    [Fact]
    public async Task DetailSpans_ShareOneInstancePerResourceAndScope_AndTheResponseListsEachOnce()
    {
        var traceId = NewTraceId();
        var rootId = NewSpanId();
        var spans = new List<SpanModel> { Span(traceId, "svc-a", "root", SpanKind.SERVER, WindowStart, 400, spanId: rootId) };
        var combos = new[] { ("svc-a", "lib-a"), ("svc-b", "lib-a"), ("svc-b", "lib-b"), ("svc-c", "lib-b"), ("svc-c", "lib-b") };
        for (var i = 1; i <= 30; i++)
        {
            var (service, library) = combos[(i - 1) % combos.Length];
            var child = Span(traceId, service, $"child-{i}", SpanKind.CLIENT, WindowStart.AddMilliseconds(10 * i), 20, parent: rootId);
            child.InstrumentationScope = SeededDataBuilder.Scope(library);
            child.Resource.Attributes["k8s.pod.annotations"] = new string('x', 2000); // a large resource, repeated per span in the old shape
            spans.Add(child);
        }
        await FlushAsync(spans);

        var detail = await DetailAsync(traceId, null);
        Assert.Equal(31, detail.Count);
        // Four resources however many spans refer to them: the root's plain svc-a, and svc-a/svc-b/svc-c with the annotation
        // the children carry (a resource is its attributes, so the root's svc-a is a different resource from child-1's).
        Assert.Equal(4, detail.Select(x => x.Resource).Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(3, detail.Select(x => x.InstrumentationScope).Distinct(ReferenceEqualityComparer.Instance).Count()); // phase0, lib-a, lib-b

        var response = Keryhe.Telemetry.Api.Models.TraceDetailResponse.From(detail);
        Assert.Equal(4, response.Resources.Count);
        Assert.Equal(3, response.Scopes.Count);
        Assert.Equal(31, response.Spans.Count);
        for (var i = 0; i < detail.Count; i++)
        {
            Assert.Same(detail[i].Resource, response.Resources[response.Spans[i].ResourceIndex]);
            Assert.Same(detail[i].InstrumentationScope, response.Scopes[response.Spans[i].ScopeIndex]);
        }

        // The point of it: the repeated attributes are sent once.
        var shared = System.Text.Json.JsonSerializer.Serialize(response).Length;
        var perSpan = System.Text.Json.JsonSerializer.Serialize(detail).Length;
        Assert.True(shared < perSpan / 3, $"shared {shared} bytes vs per-span {perSpan} bytes");
    }

    [Fact]
    public async Task RedeliveredBatch_IsStoredAgain_ButTheRowAndTraceDetailCountEachSpanOnce()
    {
        var traceId = NewTraceId();
        var rootId = NewSpanId();
        var batch = new[]
        {
            Span(traceId, "svc-a", "POST /pay", SpanKind.SERVER, WindowStart, 120, spanId: rootId),
            Span(traceId, "svc-a", "SELECT x", SpanKind.CLIENT, WindowStart.AddMilliseconds(10), 40, parent: rootId)
        };
        await FlushAsync(batch);
        await FlushAsync(batch);   // re-delivery: no unique key, so both copies are stored

        var page = await ListAsync();
        var item = Assert.Single(page.Items);        // one trace, one row
        Assert.Equal(2, item.SpanCount);              // COUNT(DISTINCT span_id), not 4

        using var scope = Scope();
        var repo = scope.ServiceProvider.GetRequiredService<ITraceReadRepository>();
        var detail = await repo.GetTraceByIdAsync(traceId);
        Assert.Equal(2, detail.Count);
        Assert.Equal(2, detail.Select(s => s.SpanIdHex).Distinct().Count());
    }

    // ---------------------------------------------------------------------------------------------
    // The capped list: order, limit and truncation
    // ---------------------------------------------------------------------------------------------

    private static List<DateTime> ExpectedStarts(IEnumerable<SpanModel> spans) =>
        spans.GroupBy(s => s.TraceIdHex)
            .Select(g => TimeConversion.UnixNanoToDateTime(g.Min(s => s.StartTimeUnixNano)))
            .ToList();

    [Theory]
    [InlineData(ListOrder.Newest)]
    [InlineData(ListOrder.Oldest)]
    public async Task TraceList_ReturnsTheRequestedEndOfTheWindow_InOrder(string order)
    {
        var spans = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 150);
        await FlushAsync(spans);
        var all = ExpectedStarts(spans);

        var result = await ListAsync(start: WindowStart, end: WindowStart.AddMinutes(5), order: order, limit: 25);

        var expected = ListOrder.IsOldest(order) ? all.Order().Take(25).ToList() : all.OrderDescending().Take(25).ToList();
        Assert.Equal(expected, result.Items.Select(t => t.TraceStartTime).ToList());
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData(ListOrder.Newest)]
    [InlineData(ListOrder.Oldest)]
    public async Task TraceList_Truncated_IsFalseAtExactlyN_AndTrueAtNPlusOne(string order)
    {
        var spans = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 40);
        await FlushAsync(spans);
        var traceCount = spans.Select(s => s.TraceIdHex).Distinct().Count();
        var end = WindowStart.AddMinutes(5);

        var exact = await ListAsync(start: WindowStart, end: end, order: order, limit: traceCount);
        Assert.Equal(traceCount, exact.Items.Count);
        Assert.False(exact.Truncated);

        var oneShort = await ListAsync(start: WindowStart, end: end, order: order, limit: traceCount - 1);
        Assert.Equal(traceCount - 1, oneShort.Items.Count);
        Assert.True(oneShort.Truncated);
    }
}
