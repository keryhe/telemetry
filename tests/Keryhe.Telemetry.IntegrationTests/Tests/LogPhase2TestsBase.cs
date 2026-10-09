using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Log list correctness (plans/list-caps.md): the list returns the newest or the oldest <c>Limit</c> rows of a
/// window in order, says whether more matched (<c>Truncated</c>, exact at N and N + 1 rows), and applies the same
/// filters to either end. Each assertion is stated against the seeded rows, so reversing the order, dropping the
/// extra-row probe or ignoring a filter fails one of them.
/// </summary>
public abstract class LogPhase2TestsBase : IAsyncLifetime
{
    private const int Count = 250;

    private readonly ProviderFixture _fixture;
    protected LogPhase2TestsBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = WindowStart.AddMinutes(10);

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    private async Task<List<LogRecordModel>> SeedAsync()
    {
        var logs = SeededDataBuilder.BasicLogWindow(_fixture.TenantId, WindowStart, Count);
        using var writeScope = Scope();
        await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(logs);
        return logs;
    }

    private static List<long?> Times(LogListResult result) => result.Items.Select(l => l.TimeUnixNano).ToList();

    [Fact]
    public async Task LogList_Newest_ReturnsTheNewestRows_NewestFirst()
    {
        var logs = await SeedAsync();
        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        var result = await repo.GetLogListAsync(new LogQuery { Start = WindowStart, End = WindowEnd, Limit = 40 });

        var expected = logs.Select(l => l.TimeUnixNano).OrderByDescending(t => t).Take(40).ToList();
        Assert.Equal(expected, Times(result));
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task LogList_Oldest_ReturnsTheOldestRows_OldestFirst()
    {
        var logs = await SeedAsync();
        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        var result = await repo.GetLogListAsync(new LogQuery { Start = WindowStart, End = WindowEnd, Limit = 40, Order = ListOrder.Oldest });

        var expected = logs.Select(l => l.TimeUnixNano).OrderBy(t => t).Take(40).ToList();
        Assert.Equal(expected, Times(result));
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData(ListOrder.Newest)]
    [InlineData(ListOrder.Oldest)]
    public async Task LogList_Truncated_IsFalseAtExactlyN_AndTrueAtNPlusOne(string order)
    {
        await SeedAsync();
        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        var exact = await repo.GetLogListAsync(new LogQuery { Start = WindowStart, End = WindowEnd, Limit = Count, Order = order });
        Assert.Equal(Count, exact.Items.Count);
        Assert.False(exact.Truncated);

        var oneShort = await repo.GetLogListAsync(new LogQuery { Start = WindowStart, End = WindowEnd, Limit = Count - 1, Order = order });
        Assert.Equal(Count - 1, oneShort.Items.Count);
        Assert.True(oneShort.Truncated);

        var roomToSpare = await repo.GetLogListAsync(new LogQuery { Start = WindowStart, End = WindowEnd, Limit = Count + 100, Order = order });
        Assert.Equal(Count, roomToSpare.Items.Count);
        Assert.False(roomToSpare.Truncated);
    }

    [Theory]
    [InlineData(ListOrder.Newest)]
    [InlineData(ListOrder.Oldest)]
    public async Task LogList_AppliesTheFiltersToEitherEnd(string order)
    {
        var logs = await SeedAsync();
        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        // Service + minimum severity: every third row is checkout-api and every fourth is ERROR (17).
        var matching = logs
            .Where(l => l.BodyValue!.Contains("from checkout-api") && l.SeverityNumber >= 13)
            .Select(l => l.TimeUnixNano)
            .ToList();
        Assert.NotEmpty(matching);

        var result = await repo.GetLogListAsync(new LogQuery
        {
            Start = WindowStart, End = WindowEnd, Service = "checkout-api", MinSeverity = 13, Limit = 1000, Order = order
        });

        var expected = ListOrder.IsOldest(order) ? matching.OrderBy(t => t).ToList() : matching.OrderByDescending(t => t).ToList();
        Assert.Equal(expected, Times(result));
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task LogList_RestrictsToTheWindow()
    {
        var logs = await SeedAsync();
        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        // The first 60 seconds only: rows 0..59 (the window's end is inclusive, so stop just short of row 60).
        var result = await repo.GetLogListAsync(new LogQuery { Start = WindowStart, End = WindowStart.AddSeconds(60).AddTicks(-1), Limit = 1000 });

        Assert.Equal(60, result.Items.Count);
        Assert.All(result.Items, l => Assert.True(l.TimeUnixNano < logs[60].TimeUnixNano));
    }
}
