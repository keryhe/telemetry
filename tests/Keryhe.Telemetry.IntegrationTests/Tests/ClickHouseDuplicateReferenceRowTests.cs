using ClickHouse.Client.ADO;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// ClickHouse's reference tables are <c>ReplacingMergeTree</c>, so their dedup is eventual: two flushes that both
/// missed the cache can store the same resource/scope/metric id twice until a merge. Reads must tolerate the
/// duplicate rows -- a plain <c>ToDictionary</c> over them used to throw, which the API answered with a 400 on every
/// logs page (it is why the stress harness's log marker probe never saw its marker on ClickHouse).
/// </summary>
[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseDuplicateReferenceRowTests(ClickHouseFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task ExecAsync(string sql)
    {
        await using var conn = new ClickHouseConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task LogPage_And_MetricSeries_Survive_Duplicate_Unmerged_Reference_Rows()
    {
        var logs = SeededDataBuilder.BasicLogWindow(fixture.TenantId, WindowStart, count: 30);
        var metrics = SeededDataBuilder.MultiInstanceGauge(fixture.TenantId, WindowStart, instanceCount: 2, pointsPerInstance: 5);
        using (var write = fixture.Services.CreateScope())
        {
            var writer = write.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
            await writer.FlushLogsAsync(logs);
            await writer.FlushMetricsAsync(metrics);
        }

        // Duplicate every reference row while merges are paused, so the duplicates are really there to be read.
        string[] tables = ["resources", "instrumentation_scopes", "metrics"];
        foreach (var table in tables) await ExecAsync($"SYSTEM STOP MERGES {table}");
        try
        {
            foreach (var table in tables) await ExecAsync($"INSERT INTO {table} SELECT * FROM {table}");

            using var read = fixture.Services.CreateScope();
            var page = await read.ServiceProvider.GetRequiredService<ILogReadRepository>().GetLogListAsync(new LogQuery
            {
                Start = WindowStart.AddMinutes(-1), End = WindowStart.AddMinutes(10), Limit = 500
            });
            Assert.Equal(logs.Count, page.Items.Count);

            var series = await read.ServiceProvider.GetRequiredService<IMetricReadRepository>().GetMetricSeriesAsync(new MetricSeriesQuery
            {
                MetricName = "phase0.cpu.utilization", Start = WindowStart.AddMinutes(-1), End = WindowStart.AddMinutes(10), Points = 10, Top = 8
            });
            Assert.NotNull(series);
            Assert.NotEmpty(series!.Series);
        }
        finally
        {
            foreach (var table in tables) await ExecAsync($"SYSTEM START MERGES {table}");
        }
    }
}
