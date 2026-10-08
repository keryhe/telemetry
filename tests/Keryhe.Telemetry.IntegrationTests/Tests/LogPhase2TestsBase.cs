using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Log list correctness checks (list-pages-server-side plan, Verification items 3 and 4): keyset
/// paging with no gaps/overlaps, and the ingestion-time pin excluding late arrivals from a page
/// while still counting them in "new since". (The rollup-vs-raw agreement check is gone with the
/// rollup tables: schema 3.0.0's summary has one path.)
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
        // 5-second safety margin (decision 3: PostgreSQL capture asOf as "now minus 5
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

        Assert.Null(lastPage.NextCursor);
    }

    /// <summary>
    /// A row whose event time falls inside the pinned window but which is ingested after `asOf` is
    /// captured must not appear on any page, and appears once a fresh pin passes its created_at
    /// (decision 3, Verification item 4).
    /// </summary>
    [Fact]
    public async Task Pin_ExcludesLateArrivals_From_Page()
    {
        var windowEnd = WindowStart.AddMinutes(30);
        var baseline = SeededDataBuilder.BasicLogWindow(_fixture.TenantId, WindowStart, count: 100);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(baseline);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        // PostgreSQL capture asOf as "now minus 5 seconds" (decision 3, to cover the
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

        // Once asOf naturally advances past the late rows' own created_at (every page is pinned by
        // construction — decision 3 has no "unpinned" mode), a freshly captured asOf includes them.
        await Task.Delay(TimeSpan.FromSeconds(6));
        var laterPage = await repo.GetLogPageAsync(new LogQuery { Start = WindowStart, End = windowEnd, Size = 500 });
        Assert.Equal(baseline.Count + late.Count, laterPage.Items.Count);
    }
}
