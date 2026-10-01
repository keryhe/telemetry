using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Retention sweeps under schema 3.0.0, each provider's own mechanism: batched per-tenant deletes
/// (PostgreSQL, SQL Server, MySQL), <c>drop_chunks</c> (Timescale) and <c>DROP PARTITION</c>
/// (ClickHouse). Data is seeded 200 and 100 days old, an hour either side of a 90-day cutoff, and
/// current; a sweep must remove what is older than the window and keep what is inside it.
///
/// Timescale and ClickHouse drop whole chunks/days, so a row just past the cutoff survives until its
/// chunk/day expires: <see cref="RowGranularRetention"/> says whether the "hour before the cutoff"
/// row is expected to be gone.
/// </summary>
public abstract class RetentionTestsBase : IAsyncLifetime
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(90);

    private readonly ProviderFixture _fixture;
    protected RetentionTestsBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>False for Timescale (chunk interval) and ClickHouse (whole days): the row an hour past the cutoff may legitimately survive.</summary>
    protected virtual bool RowGranularRetention => true;

    /// <summary>Rows of <paramref name="table"/> whose <paramref name="timeColumn"/> falls in <c>[fromNano, toNano)</c>.</summary>
    protected abstract Task<long> CountRowsAsync(string table, string timeColumn, long fromNano, long toNano);

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    private static long Nano(DateTime utc) => SeededDataBuilder.ToUnixNano(utc);

    /// <summary>The five seeded moments: 200 d, 100 d, an hour before the cutoff, an hour inside it, now.</summary>
    private static (string Name, DateTime At)[] Moments(DateTime now) =>
    [
        ("200d", now.AddDays(-200)),
        ("100d", now.AddDays(-100)),
        ("cutoff-1h", now - Window - TimeSpan.FromHours(1)),
        ("cutoff+1h", now - Window + TimeSpan.FromHours(1)),
        ("now", now)
    ];

    private async Task<Dictionary<string, long>> CountsAsync(string table, string timeColumn, DateTime now)
    {
        var result = new Dictionary<string, long>();
        foreach (var (name, at) in Moments(now))
            result[name] = await CountRowsAsync(table, timeColumn, Nano(at.AddMinutes(-10)), Nano(at.AddMinutes(10)));
        return result;
    }

    private void AssertSwept(Dictionary<string, long> before, Dictionary<string, long> after)
    {
        Assert.All(before.Values, count => Assert.True(count > 0, "every moment was seeded"));
        Assert.Equal(0, after["200d"]);
        Assert.Equal(0, after["100d"]);
        if (RowGranularRetention) Assert.Equal(0, after["cutoff-1h"]);
        Assert.Equal(before["cutoff+1h"], after["cutoff+1h"]);
        Assert.Equal(before["now"], after["now"]);
    }

    [Fact]
    public async Task TraceRetention_RemovesExpiredSpans_AndKeepsInWindowSpans()
    {
        var now = DateTime.UtcNow;
        var spans = new List<SpanModel>();
        foreach (var (name, at) in Moments(now))
            spans.AddRange(SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, at, traceCount: 4, seedOffset: Math.Abs(name.GetHashCode()) % 1_000_000));
        using (var write = Scope())
            await write.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans);

        var before = await CountsAsync("spans", "start_time_unix_nano", now);

        using var scope = Scope();
        var removed = await scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>().DeleteOldTracesAsync(Window);

        Assert.True(removed > 0);
        AssertSwept(before, await CountsAsync("spans", "start_time_unix_nano", now));
    }

    [Fact]
    public async Task LogRetention_RemovesExpiredRecords_AndKeepsInWindowRecords()
    {
        var now = DateTime.UtcNow;
        var logs = new List<LogRecordModel>();
        foreach (var (_, at) in Moments(now))
            logs.AddRange(SeededDataBuilder.BasicLogWindow(_fixture.TenantId, at, count: 20));
        using (var write = Scope())
            await write.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(logs);

        var before = await CountsAsync("log_records", "time_unix_nano", now);

        using var scope = Scope();
        var removed = await scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>().DeleteOldLogRecordsAsync(Window);

        Assert.True(removed > 0);
        AssertSwept(before, await CountsAsync("log_records", "time_unix_nano", now));
    }

    [Fact]
    public async Task MetricRetention_RemovesExpiredDataPoints_AndKeepsInWindowOnes_AndTheCatalogRow()
    {
        var now = DateTime.UtcNow;
        var metrics = new List<MetricModel>();
        foreach (var (_, at) in Moments(now))
            metrics.AddRange(SeededDataBuilder.MultiInstanceGauge(_fixture.TenantId, at, instanceCount: 1, pointsPerInstance: 6));
        using (var write = Scope())
            await write.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync(metrics);

        var before = await CountsAsync("gauge_data_points", "time_unix_nano", now);
        var catalogRowsBefore = await CountRowsAsync("metrics", "", 0, 0);

        using var scope = Scope();
        var removed = await scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>().DeleteOldMetricDataPointsAsync(Window);

        Assert.True(removed > 0);
        AssertSwept(before, await CountsAsync("gauge_data_points", "time_unix_nano", now));
        // The catalog row is never swept: a quiet metric stays listable.
        Assert.Equal(catalogRowsBefore, await CountRowsAsync("metrics", "", 0, 0));
    }

    [Fact]
    public async Task NegativeRetentionPeriod_IsRejected_RatherThanErasingEverything()
    {
        using var scope = Scope();
        var repo = scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.DeleteOldTracesAsync(TimeSpan.FromDays(-1)));
    }
}
