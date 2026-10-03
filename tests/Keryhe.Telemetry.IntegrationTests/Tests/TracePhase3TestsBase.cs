using Keryhe.Telemetry.Core;
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
/// trace; and a re-delivered span batch is stored twice but counted and shown once. Also the keyset
/// paging and ingestion-time pin checks carried over from the list-pages plan.
/// </summary>
public abstract class TracePhase3TestsBase : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    protected TracePhase3TestsBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    /// <summary>
    /// A pin far enough in the future to include everything just written, so a test exercises anchor
    /// semantics rather than the pin's own 5-second PostgreSQL/Timescale safety margin.
    /// </summary>
    private static DateTime FutureAsOf() => DateTime.UtcNow.AddMinutes(5);

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

    private async Task<TracePageResult> PageAsync(
        string? service = null, string? operation = null, string? search = null, string mode = "all",
        DateTime? start = null, DateTime? end = null, DateTime? asOf = null, bool useDbAsOf = false, double? minDurationMs = null)
    {
        using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<ITraceReadRepository>().GetTracePageAsync(new TraceQuery
        {
            Start = start ?? WindowStart.AddMinutes(-1),
            End = end ?? WindowStart.AddHours(1),
            Service = service, Operation = operation, Search = search, Mode = mode, Size = 500,
            AsOf = useDbAsOf ? null : asOf ?? FutureAsOf(), MinDurationMs = minDurationMs
        });
    }

    private async Task<TraceSummaryResult> SummaryAsync(
        string? service = null, string mode = "all", DateTime? start = null, DateTime? end = null, double? minDurationMs = null,
        DateTime? asOf = null)
    {
        using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<ITraceReadRepository>().GetTraceSummaryAsync(new TraceSummaryQuery
        {
            Start = start ?? WindowStart.AddMinutes(-1),
            End = end ?? WindowStart.AddHours(1),
            Service = service, Mode = mode, BucketCount = 4, AsOf = asOf ?? FutureAsOf(), MinDurationMs = minDurationMs
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

        var page = await PageAsync();

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
    public async Task RootArrivingLater_BecomesTheAnchorOnTheNextFreshQuery_NotWithinAPinnedAsOf()
    {
        var traceId = NewTraceId();
        await FlushAsync(Span(traceId, "svc-a", "late-span", SpanKind.SERVER, WindowStart.AddSeconds(5), 30, parent: NewSpanId()));

        // Wait past PostgreSQL/Timescale's 5-second "now minus 5s" pin margin, then pin.
        await Task.Delay(TimeSpan.FromSeconds(6));
        var pinned = await PageAsync(useDbAsOf: true);
        var asOf = pinned.AsOf;
        Assert.Equal("late-span", Assert.Single(pinned.Items).RootOperationName);

        // The true root (earlier start) arrives after the pin.
        await FlushAsync(Span(traceId, "svc-a", "the-root", SpanKind.SERVER, WindowStart, 500));

        var stillPinned = await PageAsync(asOf: asOf);
        Assert.Equal("late-span", Assert.Single(stillPinned.Items).RootOperationName);

        await Task.Delay(TimeSpan.FromSeconds(6));
        var fresh = await PageAsync(useDbAsOf: true);
        var item = Assert.Single(fresh.Items);
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

        var backend = Assert.Single((await PageAsync(service: "svc-backend")).Items);
        Assert.Equal(traceId, backend.TraceIdHex);
        Assert.Equal("svc-backend", backend.ServiceName);
        Assert.Equal("handle", backend.RootOperationName);
        Assert.Equal(TimeSpan.FromMilliseconds(100), backend.TraceDuration);
        Assert.Equal(2, backend.SpanCount);          // the service's own spans, not the whole trace's four
        Assert.False(backend.HasErrors);              // the error span belongs to another service

        var db = Assert.Single((await PageAsync(service: "svc-db")).Items);
        Assert.Equal("CLIENT", db.AnchorKind);
        Assert.True(db.HasErrors);
        Assert.Equal(1, db.SpanCount);

        var whole = Assert.Single((await PageAsync()).Items);
        Assert.Equal("svc-gateway", whole.ServiceName);
        Assert.Equal(4, whole.SpanCount);
        Assert.True(whole.HasErrors);
    }

    [Fact]
    public async Task SlowFilter_SummaryPercentiles_AndRowDuration_AllUseTheAnchorsOwnDuration()
    {
        var slow = NewTraceId();
        var fast = NewTraceId();
        await FlushAsync(
            Span(slow, "svc-a", "slow-op", SpanKind.SERVER, WindowStart, 1234),
            // The fast trace's anchor is short even though a late child makes the whole trace long.
            Span(fast, "svc-a", "fast-op", SpanKind.SERVER, WindowStart.AddSeconds(1), 20),
            Span(fast, "svc-a", "long-tail", SpanKind.CLIENT, WindowStart.AddSeconds(1).AddMilliseconds(5), 5000));

        var page = await PageAsync(mode: "slow", minDurationMs: 1000);
        var row = Assert.Single(page.Items);
        Assert.Equal(slow, row.TraceIdHex);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), row.TraceDuration);

        var summary = await SummaryAsync(mode: "slow", minDurationMs: 1000);
        Assert.Equal(1, summary.Summary.Count);
        Assert.Equal(1234, summary.Summary.P50Ms, 0.5);
        Assert.Equal(1, summary.ListTotal);

        // Unfiltered, the percentiles are over both anchors' own durations (20 ms and 1234 ms).
        var all = await SummaryAsync();
        Assert.Equal(2, all.Summary.Count);
        Assert.Equal(1234, all.Summary.P99Ms, 0.5);
    }

    [Fact]
    public async Task OperationFilter_MatchesTheAnchorsNameOnly()
    {
        var traceId = NewTraceId();
        var rootId = NewSpanId();
        await FlushAsync(
            Span(traceId, "svc-a", "POST /pay", SpanKind.SERVER, WindowStart, 200, spanId: rootId),
            Span(traceId, "svc-a", "GET /cart", SpanKind.CLIENT, WindowStart.AddMilliseconds(20), 50, parent: rootId));

        Assert.Empty((await PageAsync(operation: "GET /cart")).Items);
        Assert.Equal(traceId, Assert.Single((await PageAsync(operation: "POST /pay")).Items).TraceIdHex);
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

        var unscoped = await PageAsync(mode: "errors");
        Assert.Equal(errTrace, Assert.Single(unscoped.Items).TraceIdHex);

        // Scoped to svc-a the error span (svc-b's) is out of scope, so the trace is not an error row.
        Assert.Empty((await PageAsync(service: "svc-a", mode: "errors")).Items);
        Assert.Equal(errTrace, Assert.Single((await PageAsync(service: "svc-b", mode: "errors")).Items).TraceIdHex);
    }

    [Fact]
    public async Task Search_WithAServiceSelected_FindsATraceWhoseMatchingSpanBelongsToAnotherService()
    {
        var traceId = NewTraceId();
        var gatewayId = NewSpanId();
        await FlushAsync(
            Span(traceId, "svc-gateway", "GET /api", SpanKind.SERVER, WindowStart, 300, spanId: gatewayId),
            Span(traceId, "svc-backend", "rare-needle-span", SpanKind.SERVER, WindowStart.AddMilliseconds(50), 100, parent: gatewayId));

        var page = await PageAsync(service: "svc-gateway", search: "rare-needle");
        var item = Assert.Single(page.Items);
        Assert.Equal(traceId, item.TraceIdHex);
        // Still anchored on the SELECTED service's earliest span, not on the span that matched.
        Assert.Equal("svc-gateway", item.ServiceName);
        Assert.Equal("GET /api", item.RootOperationName);

        Assert.Empty((await PageAsync(service: "svc-gateway", search: "no-such-text-anywhere")).Items);
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

        var page = await PageAsync(start: windowStart, end: windowEnd);
        Assert.Equal(inside, Assert.Single(page.Items).TraceIdHex);
    }

    [Fact]
    public async Task RequestCountCard_CountsOnlyInboundAnchors_WhileTheListShowsEveryKind()
    {
        var kinds = new[] { SpanKind.SERVER, SpanKind.CONSUMER, SpanKind.CLIENT, SpanKind.INTERNAL };
        await FlushAsync(kinds.Select((kind, i) =>
            Span(NewTraceId(), "svc-a", $"op-{kind}", kind, WindowStart.AddSeconds(i), 30)));

        var page = await PageAsync();
        Assert.Equal(4, page.Items.Count);
        Assert.Equal(kinds.Select(k => k.ToString()).Order(), page.Items.Select(t => t.AnchorKind!).Order());

        var summary = await SummaryAsync();
        Assert.Equal(2, summary.Summary.Count);
        Assert.Equal(2, summary.RequestCount);
        Assert.Equal(4, summary.ListTotal);
    }

    /// <summary>
    /// <c>listTotal</c> rides along with the inbound rows of the single summary pass; with no inbound anchor at all
    /// there is no row to carry it, and the separate count must still report every anchor.
    /// </summary>
    [Fact]
    public async Task Summary_WithNoInboundAnchors_StillReportsTheAllKindsListTotal()
    {
        await FlushAsync(
            Span(NewTraceId(), "svc-a", "client-op", SpanKind.CLIENT, WindowStart, 10),
            Span(NewTraceId(), "svc-a", "internal-op", SpanKind.INTERNAL, WindowStart.AddSeconds(1), 10));

        var summary = await SummaryAsync();
        Assert.Equal(0, summary.RequestCount);
        Assert.Equal(0, summary.Summary.Count);
        Assert.Equal(2, summary.ListTotal);

        var empty = await SummaryAsync(start: WindowStart.AddDays(2), end: WindowStart.AddDays(2).AddHours(1));
        Assert.Equal(0, empty.ListTotal);
    }

    /// <summary>
    /// The summary is pinned on <c>asOf</c> like the page (3.0.1): the chart, the cards and <c>listTotal</c> describe the
    /// same traces the pinned list can show, so a trace that arrives after the pin is in none of them. Negative
    /// control: a pin past its arrival includes it everywhere.
    /// </summary>
    [Fact]
    public async Task Summary_IsPinnedOnAsOf_ChartCardsAndTotalExcludeLateArrivals()
    {
        var baseline = Enumerable.Range(0, 3).Select(i => Span(NewTraceId(), "svc-a", "base", SpanKind.SERVER, WindowStart.AddSeconds(i), 40)).ToArray();
        await FlushAsync(baseline);

        // Same PostgreSQL/Timescale 5-second pin margin as the other pin tests.
        await Task.Delay(TimeSpan.FromSeconds(6));
        var asOf = (await PageAsync(useDbAsOf: true)).AsOf;

        await FlushAsync(Enumerable.Range(0, 2).Select(i => Span(NewTraceId(), "svc-a", "late", SpanKind.SERVER, WindowStart.AddSeconds(10 + i), 40)));

        var pinned = await SummaryAsync(asOf: asOf);
        Assert.Equal(asOf, pinned.AsOf);
        Assert.Equal(3, pinned.ListTotal);
        Assert.Equal(3, pinned.RequestCount);
        Assert.Equal(3, pinned.Summary.Count);
        Assert.Equal(3, pinned.Buckets.Sum(b => b.Count));

        var unpinned = await SummaryAsync(asOf: FutureAsOf());
        Assert.Equal(5, unpinned.ListTotal);
        Assert.Equal(5, unpinned.RequestCount);
        Assert.Equal(5, unpinned.Buckets.Sum(b => b.Count));
    }

    // ---------------------------------------------------------------------------------------------
    // Sliced paging (trace-list-detail-performance plan, Phase 4): the page is read from slices of the
    // window instead of ranking all of it, and must agree with the anchor DEFINITION for every nav,
    // filter, page size and slice width. The expected rows are computed here in LINQ from the seeded
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
    public async Task SlicedPaging_MatchesTheAnchorDefinition_ForEveryNavFilterPageSizeAndSliceWidth()
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
        // One pin for the whole test: a cursor carries a hash of the filter including asOf.
        var pin = FutureAsOf();

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

                foreach (var size in new[] { 7, 50 })
                {
                    string Context(string nav) => $"slice={sliceSeconds}s x{growth}, filter={f.Name}, size={size}, {nav}";
                    TraceQuery Q(string nav, string? cursor = null) => new()
                    {
                        Start = windowStart, End = windowEnd, Service = f.Service, Operation = f.Operation, Mode = f.Mode, Search = f.Search,
                        MinDurationMs = f.Mode == "slow" ? 500 : null, Size = size, Nav = nav, Cursor = cursor, AsOf = pin,
                    };

                    // Forward: first, then next until the end.
                    var forward = new List<TraceInfo>();
                    var page = await repo.GetTracePageAsync(Q("first"));
                    forward.AddRange(page.Items);
                    for (var guard = 0; page.NextCursor != null && guard < 100; guard++)
                    {
                        page = await repo.GetTracePageAsync(Q("next", page.NextCursor));
                        forward.AddRange(page.Items);
                    }

                    // Backward: last, then prev until the start; pages are prepended to restore the descending order.
                    var backward = new List<TraceInfo>();
                    page = await repo.GetTracePageAsync(Q("last"));
                    backward.InsertRange(0, page.Items);
                    for (var guard = 0; page.PrevCursor != null && guard < 100; guard++)
                    {
                        page = await repo.GetTracePageAsync(Q("prev", page.PrevCursor));
                        backward.InsertRange(0, page.Items);
                    }

                    foreach (var (direction, items) in new[] { ("forward", forward), ("backward", backward) })
                    {
                        var ctx = Context(direction);
                        Assert.True(expected.Count == items.Count, $"{ctx}: expected {expected.Count} traces, got {items.Count}");
                        Assert.Equal(expected.Keys.Order(), items.Select(i => i.TraceIdHex).Order());

                        long previousStart = long.MaxValue;
                        foreach (var item in items)
                        {
                            var e = expected[item.TraceIdHex];
                            var anchorStart = spansById[item.DisplaySpanIdHex!].StartTimeUnixNano;
                            Assert.True(anchorStart <= previousStart, $"{ctx}: rows are not in descending anchor-start order");
                            previousStart = anchorStart;
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
    }

    // ---------------------------------------------------------------------------------------------
    // Trace detail (trace-list-detail-performance plan, Phase 6): the start-time hint and the shared resources/scopes.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Whether this provider reads only the range of a trace-time hint (Timescale's chunks, ClickHouse's partitions); the others ignore it.</summary>
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
    public async Task RedeliveredBatch_IsStoredAgain_ButTheRowCountAndTraceDetailCountEachSpanOnce()
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

        var page = await PageAsync();
        var item = Assert.Single(page.Items);        // one trace, one row
        Assert.Equal(2, item.SpanCount);              // COUNT(DISTINCT span_id), not 4

        using var scope = Scope();
        var repo = scope.ServiceProvider.GetRequiredService<ITraceReadRepository>();
        var detail = await repo.GetTraceByIdAsync(traceId);
        Assert.Equal(2, detail.Count);
        Assert.Equal(2, detail.Select(s => s.SpanIdHex).Distinct().Count());

        var summary = await SummaryAsync();
        Assert.Equal(1, summary.Summary.Count);
    }

    // ---------------------------------------------------------------------------------------------
    // Paging and the ingestion-time pin
    // ---------------------------------------------------------------------------------------------

    /// <summary>Paging forward through every anchor then back reproduces the same rows in reverse, with no gaps/duplicates (mirrors LogPhase2TestsBase's identical check).</summary>
    [Fact]
    public async Task TracePage_Keyset_Forward_Then_Back_NoGapsOrOverlaps()
    {
        var spans = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 150);
        await FlushAsync(spans);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ITraceReadRepository>();
        var windowEnd = WindowStart.AddMinutes(5);
        var expectedTraceCount = spans.Select(s => s.TraceIdHex).Distinct().Count();

        const int size = 25;
        var asOf = (DateTime?)DateTime.UtcNow.AddMinutes(1);
        var forwardPages = new List<TracePageResult>();
        string? cursor = null;
        var nav = "first";
        for (var i = 0; i < 10 && forwardPages.Sum(p => p.Items.Count) < expectedTraceCount; i++)
        {
            var page = await repo.GetTracePageAsync(new TraceQuery
            {
                Start = WindowStart, End = windowEnd, Size = size, Cursor = cursor, Nav = nav, AsOf = asOf, Mode = "all"
            });
            forwardPages.Add(page);
            if (page.NextCursor == null) break;
            cursor = page.NextCursor;
            nav = "next";
        }

        var forwardIds = forwardPages.SelectMany(p => p.Items.Select(t => t.TraceIdHex)).ToList();
        Assert.Equal(expectedTraceCount, forwardIds.Count);
        Assert.Equal(forwardIds.Distinct().Count(), forwardIds.Count);

        var forwardStarts = forwardPages.SelectMany(p => p.Items.Select(t => t.TraceStartTime)).ToList();
        Assert.Equal(forwardStarts, forwardStarts.OrderByDescending(x => x));

        var lastPage = forwardPages[^1];
        if (forwardPages.Count > 1 && lastPage.PrevCursor != null)
        {
            var backPage = await repo.GetTracePageAsync(new TraceQuery
            {
                Start = WindowStart, End = windowEnd, Size = size, Cursor = lastPage.PrevCursor, Nav = "prev", AsOf = asOf, Mode = "all"
            });
            var expected = forwardPages[^2].Items.Select(t => t.TraceIdHex).ToList();
            var actual = backPage.Items.Select(t => t.TraceIdHex).ToList();
            Assert.Equal(expected, actual);
        }

        var last = await repo.GetTracePageAsync(new TraceQuery { Start = WindowStart, End = windowEnd, Size = size, Nav = "last", AsOf = asOf, Mode = "all" });
        Assert.NotEmpty(last.Items);
        Assert.Null(last.NextCursor);
    }

    /// <summary>A trace whose event time is inside the pinned window but which arrives after `asOf` is excluded from a pinned page and included once a fresh pin passes it (mirrors LogPhase2TestsBase's identical check).</summary>
    [Fact]
    public async Task Pin_ExcludesLateArrivals_From_Page()
    {
        var windowEnd = WindowStart.AddMinutes(30);
        var baseline = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 30);
        await FlushAsync(baseline);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ITraceReadRepository>();

        // Same PostgreSQL/Timescale transaction-start-race margin as the log test.
        await Task.Delay(TimeSpan.FromSeconds(6));

        var firstPage = await repo.GetTracePageAsync(new TraceQuery { Start = WindowStart, End = windowEnd, Size = 500, Mode = "all" });
        var asOf = firstPage.AsOf;
        var baselineTraceCount = baseline.Select(s => s.TraceIdHex).Distinct().Count();
        Assert.Equal(baselineTraceCount, firstPage.Items.Count);

        // Distinct trace ids (seedOffset) so the "late" batch doesn't collide with the baseline.
        var late = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart.AddSeconds(1), traceCount: 10, seedOffset: 100_000);
        await FlushAsync(late);
        var lateTraceCount = late.Select(s => s.TraceIdHex).Distinct().Count();

        var pinnedPage = await repo.GetTracePageAsync(new TraceQuery { Start = WindowStart, End = windowEnd, Size = 500, AsOf = asOf, Mode = "all" });
        Assert.Equal(baselineTraceCount, pinnedPage.Items.Count);

        await Task.Delay(TimeSpan.FromSeconds(6));
        var laterPage = await repo.GetTracePageAsync(new TraceQuery { Start = WindowStart, End = windowEnd, Size = 500, Mode = "all" });
        Assert.Equal(baselineTraceCount + lateTraceCount, laterPage.Items.Count);
    }
}
