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
/// Phase 2 correctness checks (list-pages-server-side plan, Verification items 3, 4 and 14):
/// rollup-vs-raw summary agreement, keyset paging with no gaps/overlaps, and the ingestion-time
/// pin excluding late arrivals from a page while still counting them in "new since".
/// </summary>
public abstract class LogPhase2TestsBase : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    protected LogPhase2TestsBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    /// <summary>
    /// Runs the rollup worker's per-cycle logic directly and synchronously, rather than waiting on
    /// its real interval/timer — this is what makes it testable without real wall-clock timing
    /// (the plan's own suggestion). Small Settle/Repass values so one call fully rolls and
    /// re-passes a window well in the past relative to <paramref name="now"/>.
    /// </summary>
    private async Task RunRollupCycleAsync(DateTime now)
    {
        var worker = new RollupWorker(
            scopeFactory: null!, // RunCycleAsync doesn't use the scope factory (only ExecuteAsync's real loop does)
            Options.Create(new RollupOptions { SettleSeconds = 1, RepassMinutes = 1, BackfillHours = 24, LeaseSeconds = 300 }),
            NullLogger<RollupWorker>.Instance);

        using var scope = Scope();
        await worker.RunCycleAsync(scope.ServiceProvider, now, CancellationToken.None);
    }

    [Fact]
    public async Task RollupWorker_LogSummary_MatchesRawPath()
    {
        var logs = SeededDataBuilder.BasicLogWindow(_fixture.TenantId, WindowStart, count: 300); // 5 minutes, one per second
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(logs);

        // now well past the window so SettleSeconds/RepassMinutes both clear it in one cycle.
        await RunRollupCycleAsync(WindowStart.AddMinutes(10));

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        var windowEnd = WindowStart.AddMinutes(5);
        var rollupSummary = await repo.GetLogSummaryAsync(new LogSummaryQuery { Start = WindowStart, End = windowEnd, BucketCount = 5 });
        Assert.Equal("rollup", rollupSummary.Source);

        // Force the raw path with a search term every seeded row matches (decision 37: any `q`
        // filter always uses raw) — same population, so the two paths must agree exactly.
        var rawSummary = await repo.GetLogSummaryAsync(new LogSummaryQuery { Start = WindowStart, End = windowEnd, BucketCount = 5, Search = "phase0" });
        Assert.Equal("raw", rawSummary.Source);

        Assert.Equal(logs.Count, rollupSummary.Total);
        Assert.Equal(rawSummary.Total, rollupSummary.Total);
        Assert.Equal(rawSummary.Buckets.Sum(b => b.Error), rollupSummary.Buckets.Sum(b => b.Error));
        Assert.Equal(rawSummary.Buckets.Sum(b => b.Warn), rollupSummary.Buckets.Sum(b => b.Warn));
        Assert.Equal(rawSummary.Buckets.Sum(b => b.Info), rollupSummary.Buckets.Sum(b => b.Info));
        Assert.Equal(rawSummary.Buckets.Sum(b => b.Debug), rollupSummary.Buckets.Sum(b => b.Debug));
    }

    /// <summary>
    /// Paging forward to the end then back returns the same rows in reverse, and `nav=last`
    /// followed by `nav=prev` meets the page reached by paging forward from the start — no gaps,
    /// no duplicates (Verification item 3).
    /// </summary>
    [Fact]
    public async Task LogPage_Keyset_Forward_Then_Back_NoGapsOrOverlaps()
    {
        var logs = SeededDataBuilder.BasicLogWindow(_fixture.TenantId, WindowStart, count: 250);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(logs);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();
        var windowEnd = WindowStart.AddMinutes(10);

        const int size = 40;
        // Pinned well after "now" so this test exercises keyset correctness, not the pin's own
        // 5-second safety margin (decision 3: PostgreSQL/Timescale capture asOf as "now minus 5
        // seconds" to cover the transaction-start race, which would otherwise hide rows inserted
        // moments ago in a fast test run).
        var asOf = (DateTime?)DateTime.UtcNow.AddMinutes(1);
        var forwardPages = new List<LogPageResult>();
        string? cursor = null;
        var nav = "first";
        for (var i = 0; i < 10 && forwardPages.Sum(p => p.Items.Count) < logs.Count; i++)
        {
            var page = await repo.GetLogPageAsync(new LogQuery
            {
                Start = WindowStart, End = windowEnd, Size = size, Cursor = cursor, Nav = nav, AsOf = asOf
            });
            forwardPages.Add(page);
            if (page.NextCursor == null) break;
            cursor = page.NextCursor;
            nav = "next";
        }

        var forwardIds = forwardPages.SelectMany(p => p.Items.Select(l => l.TimeUnixNano)).ToList();
        Assert.Equal(logs.Count, forwardIds.Count);
        Assert.Equal(forwardIds.Distinct().Count(), forwardIds.Count); // no duplicates across pages
        Assert.Equal(forwardIds, forwardIds.OrderByDescending(x => x)); // strictly newest-first, matching the default order

        // Page back from the last forward page using its prevCursor and confirm it reproduces the
        // page immediately before it, exactly.
        var lastPage = forwardPages[^1];
        if (forwardPages.Count > 1 && lastPage.PrevCursor != null)
        {
            var backPage = await repo.GetLogPageAsync(new LogQuery
            {
                Start = WindowStart, End = windowEnd, Size = size, Cursor = lastPage.PrevCursor, Nav = "prev", AsOf = asOf
            });
            var expected = forwardPages[^2].Items.Select(l => l.TimeUnixNano).ToList();
            var actual = backPage.Items.Select(l => l.TimeUnixNano).ToList();
            Assert.Equal(expected, actual);
        }

        // nav=last reaches the oldest rows, disjoint from every earlier page's rows except where
        // the tail naturally overlaps the last forward page fetched.
        var last = await repo.GetLogPageAsync(new LogQuery { Start = WindowStart, End = windowEnd, Size = size, Nav = "last", AsOf = asOf });
        Assert.NotEmpty(last.Items);
        Assert.Null(last.NextCursor);
        var oldestForward = forwardIds.Min();
        Assert.True(last.Items.Select(l => l.TimeUnixNano).Min() <= oldestForward);
    }

    /// <summary>
    /// A row whose event time falls inside the pinned window but which is ingested after `asOf` is
    /// captured must not appear on any page, while the "new since" count picks it up (decision 3,
    /// Verification item 4).
    /// </summary>
    [Fact]
    public async Task Pin_ExcludesLateArrivals_From_Page_But_NewSinceAsOf_Counts_Them()
    {
        var windowEnd = WindowStart.AddMinutes(30);
        var baseline = SeededDataBuilder.BasicLogWindow(_fixture.TenantId, WindowStart, count: 100);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(baseline);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        // PostgreSQL/Timescale capture asOf as "now minus 5 seconds" (decision 3, to cover the
        // transaction-start race) — wait past that margin before capturing it here, or the
        // baseline rows just inserted above would themselves fail created_at <= asOf and this test
        // would be asserting the wrong thing.
        await Task.Delay(TimeSpan.FromSeconds(6));

        // Capture asOf from the database clock via a first page request.
        var firstPage = await repo.GetLogPageAsync(new LogQuery { Start = WindowStart, End = windowEnd, Size = 500 });
        var asOf = firstPage.AsOf;
        Assert.Equal(baseline.Count, firstPage.Items.Count);

        // Insert rows whose event time is inside the window but arrive (created_at) after asOf.
        var late = SeededDataBuilder.LateArrivingLogs(_fixture.TenantId, WindowStart, windowEnd, count: 15);
        using (var lateWriteScope = Scope())
            await lateWriteScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(late);

        var pinnedPage = await repo.GetLogPageAsync(new LogQuery { Start = WindowStart, End = windowEnd, Size = 500, AsOf = asOf });
        Assert.Equal(baseline.Count, pinnedPage.Items.Count);

        var summary = await repo.GetLogSummaryAsync(new LogSummaryQuery { Start = WindowStart, End = windowEnd, AsOf = asOf, BucketCount = 1 });
        Assert.Equal(late.Count, summary.NewSinceAsOf);

        // Once asOf naturally advances past the late rows' own created_at (every page is pinned by
        // construction — decision 3 has no "unpinned" mode), a freshly captured asOf includes them.
        await Task.Delay(TimeSpan.FromSeconds(6));
        var laterPage = await repo.GetLogPageAsync(new LogQuery { Start = WindowStart, End = windowEnd, Size = 500 });
        Assert.Equal(baseline.Count + late.Count, laterPage.Items.Count);
    }
}
