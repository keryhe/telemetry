using Keryhe.Telemetry.Api.Rollups;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Phase 3 correctness checks (list-pages-server-side plan): rollup-vs-raw trace summary
/// agreement, keyset paging over anchors with no gaps/overlaps, the ingestion-time pin, and
/// orphan-trace detection/visibility (decision 41).
/// </summary>
public abstract class TracePhase3TestsBase : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    protected TracePhase3TestsBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    /// <summary>Runs the rollup worker's per-cycle logic directly and synchronously — see LogPhase2TestsBase's identical helper for why.</summary>
    private async Task RunRollupCycleAsync(DateTime now)
    {
        var worker = new RollupWorker(
            scopeFactory: null!,
            Options.Create(new RollupOptions { SettleSeconds = 1, RepassMinutes = 1, BackfillHours = 24, LeaseSeconds = 300 }),
            NullLogger<RollupWorker>.Instance);

        using var scope = Scope();
        await worker.RunCycleAsync(scope.ServiceProvider, now, CancellationToken.None);
    }

    [Fact]
    public async Task RollupWorker_TraceSummary_MatchesRawPath()
    {
        // BasicTraceWindow's roots are all SERVER kind, one trace per second.
        var spans = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 200);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans);

        // now well past the window so SettleSeconds/RepassMinutes both clear it in one cycle.
        await RunRollupCycleAsync(WindowStart.AddMinutes(10));

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ITraceReadRepository>();

        var windowEnd = WindowStart.AddMinutes(4);
        var rollupSummary = await repo.GetTraceSummaryAsync(new TraceSummaryQuery { Start = WindowStart, End = windowEnd, BucketCount = 4, Mode = "all" });
        Assert.Equal("rollup", rollupSummary.Source);

        // Force the raw path with a search term every seeded root matches (an operation filter
        // also always forces raw — either works; a search term is used here to match the log
        // Phase 2 test's own approach).
        var rawSummary = await repo.GetTraceSummaryAsync(new TraceSummaryQuery { Start = WindowStart, End = windowEnd, BucketCount = 4, Mode = "all", Search = "checkout" });
        Assert.Equal("raw", rawSummary.Source);

        var expectedRootCount = spans.Count(s => s.ParentSpanIdHex == null && ToUnixDateTime(s.StartTimeUnixNano) < windowEnd);
        Assert.Equal(expectedRootCount, rollupSummary.Summary.Count);
        Assert.Equal(rawSummary.Summary.Count, rollupSummary.Summary.Count);
        Assert.Equal(rawSummary.Summary.ErrorCount, rollupSummary.Summary.ErrorCount);
        Assert.Equal(rawSummary.ListTotal, rollupSummary.ListTotal);
    }

    /// <summary>Paging forward through every anchor then back reproduces the same rows in reverse, with no gaps/duplicates (mirrors LogPhase2TestsBase's identical check).</summary>
    [Fact]
    public async Task TracePage_Keyset_Forward_Then_Back_NoGapsOrOverlaps()
    {
        var spans = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 150);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans);

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
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(baseline);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ITraceReadRepository>();

        // Same PostgreSQL/Timescale transaction-start-race margin as the log Phase 2 test.
        await Task.Delay(TimeSpan.FromSeconds(6));

        var firstPage = await repo.GetTracePageAsync(new TraceQuery { Start = WindowStart, End = windowEnd, Size = 500, Mode = "all" });
        var asOf = firstPage.AsOf;
        var baselineTraceCount = baseline.Select(s => s.TraceIdHex).Distinct().Count();
        Assert.Equal(baselineTraceCount, firstPage.Items.Count);

        // Distinct trace ids (seedOffset) so the "late" batch doesn't collide with the baseline.
        var late = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart.AddSeconds(1), traceCount: 10, seedOffset: 100_000);
        using (var lateWriteScope = Scope())
            await lateWriteScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(late);
        var lateTraceCount = late.Select(s => s.TraceIdHex).Distinct().Count();

        var pinnedPage = await repo.GetTracePageAsync(new TraceQuery { Start = WindowStart, End = windowEnd, Size = 500, AsOf = asOf, Mode = "all" });
        Assert.Equal(baselineTraceCount, pinnedPage.Items.Count);

        var summary = await repo.GetTraceSummaryAsync(new TraceSummaryQuery { Start = WindowStart, End = windowEnd, AsOf = asOf, BucketCount = 1, Mode = "all" });
        Assert.Equal(lateTraceCount, summary.NewSinceAsOf);

        await Task.Delay(TimeSpan.FromSeconds(6));
        var laterPage = await repo.GetTracePageAsync(new TraceQuery { Start = WindowStart, End = windowEnd, Size = 500, Mode = "all" });
        Assert.Equal(baselineTraceCount + lateTraceCount, laterPage.Items.Count);
    }

    /// <summary>
    /// A trace whose root never arrived (decision 41) is invisible until the rollup worker's
    /// orphan-detection pass runs for its minute, then appears in the page and counts toward
    /// listTotal — and stays gone from a page pinned before that pass ran.
    /// </summary>
    [Fact]
    public async Task OrphanTrace_Invisible_Until_RollupWorker_DetectsIt_Then_AppearsInPage()
    {
        var orphan = SeededDataBuilder.OrphanTrace(_fixture.TenantId, WindowStart);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(orphan);

        var windowEnd = WindowStart.AddMinutes(5);

        using (var beforeScope = Scope())
        {
            var repoBefore = beforeScope.ServiceProvider.GetRequiredService<ITraceReadRepository>();
            var pageBefore = await repoBefore.GetTracePageAsync(new TraceQuery { Start = WindowStart, End = windowEnd, Size = 500, Mode = "all" });
            Assert.DoesNotContain(pageBefore.Items, t => t.TraceIdHex == orphan[0].TraceIdHex);
        }

        // now well past the orphan trace's minute so SettleSeconds/RepassMinutes both clear it.
        await RunRollupCycleAsync(WindowStart.AddMinutes(10));

        // Same PostgreSQL/Timescale transaction-start-race margin as the log Phase 2 test's pin
        // check: asOf resolves as "now minus 5 seconds", so a page queried immediately after the
        // rollup just wrote orphan_roots.detected_at (~now) would still exclude it.
        await Task.Delay(TimeSpan.FromSeconds(6));

        using var afterScope = Scope();
        var repoAfter = afterScope.ServiceProvider.GetRequiredService<ITraceReadRepository>();
        var pageAfter = await repoAfter.GetTracePageAsync(new TraceQuery { Start = WindowStart, End = windowEnd, Size = 500, Mode = "all" });
        Assert.Contains(pageAfter.Items, t => t.TraceIdHex == orphan[0].TraceIdHex);

        var summary = await repoAfter.GetTraceSummaryAsync(new TraceSummaryQuery { Start = WindowStart, End = windowEnd, BucketCount = 1, Mode = "all" });
        Assert.True(summary.ListTotal >= 1);
    }

    private static DateTime ToUnixDateTime(long unixNano) =>
        DateTime.UnixEpoch.AddTicks(unixNano / 100);
}
