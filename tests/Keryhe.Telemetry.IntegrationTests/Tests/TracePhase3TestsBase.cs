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
        string? service = null, string mode = "all", DateTime? start = null, DateTime? end = null, double? minDurationMs = null)
    {
        using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<ITraceReadRepository>().GetTraceSummaryAsync(new TraceSummaryQuery
        {
            Start = start ?? WindowStart.AddMinutes(-1),
            End = end ?? WindowStart.AddHours(1),
            Service = service, Mode = mode, BucketCount = 4, AsOf = FutureAsOf(), MinDurationMs = minDurationMs
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

    /// <summary>A trace whose event time is inside the pinned window but which arrives after `asOf` is excluded from a page while "new since" counts it (mirrors LogPhase2TestsBase's identical check).</summary>
    [Fact]
    public async Task Pin_ExcludesLateArrivals_From_Page_But_NewSinceAsOf_Counts_Them()
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

        var summary = await repo.GetTraceSummaryAsync(new TraceSummaryQuery { Start = WindowStart, End = windowEnd, AsOf = asOf, BucketCount = 1, Mode = "all" });
        Assert.Equal(lateTraceCount, summary.NewSinceAsOf);

        await Task.Delay(TimeSpan.FromSeconds(6));
        var laterPage = await repo.GetTracePageAsync(new TraceQuery { Start = WindowStart, End = windowEnd, Size = 500, Mode = "all" });
        Assert.Equal(baselineTraceCount + lateTraceCount, laterPage.Items.Count);
    }
}
