using ClickHouse.Client.ADO;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseRetentionTests(ClickHouseFixture fixture) : RetentionTestsBase(fixture)
{
    private readonly ClickHouseFixture _db = fixture;

    // DROP PARTITION removes whole days: a row just past the cutoff survives until its day is wholly expired.
    protected override bool RowGranularRetention => false;

    // The base names the relational tables and columns; the ClickHouse row model has its own (a DateTime64 time
    // column, "_points" tables, "metric_catalog").
    protected override async Task<long> CountRowsAsync(string table, string timeColumn, long fromNano, long toNano)
    {
        await using var conn = new ClickHouseConnection(_db.DatabaseConnectionString);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = (table, timeColumn) switch
        {
            ("metrics", _) => "SELECT count() FROM metric_catalog",
            ("spans", _) => $"SELECT count() FROM spans WHERE {Range("start_time", fromNano, toNano)}",
            ("log_records", _) => $"SELECT count() FROM log_records WHERE {Range("timestamp", fromNano, toNano)}",
            ("gauge_data_points", _) => $"SELECT count() FROM gauge_points WHERE {Range("time", fromNano, toNano)}",
            ("request_rollup_minute", _) or ("log_rollup_minute", _) =>
                $"SELECT count() FROM {table} WHERE bucket_start_unix_nano >= {fromNano} AND bucket_start_unix_nano < {toNano}",
            _ => throw new NotSupportedException(table)
        };
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static string Range(string column, long fromNano, long toNano)
        => $"{column} >= fromUnixTimestamp64Nano({fromNano}, 'UTC') AND {column} < fromUnixTimestamp64Nano({toNano}, 'UTC')";

    [Fact]
    public async Task MetricRetention_DeletesExpiredSeriesAndCatalogRows_AndKeepsCurrentOnes()
    {
        await using var conn = new ClickHouseConnection(_db.DatabaseConnectionString);
        await conn.OpenAsync();
        async Task Exec(string sql) { var c = conn.CreateCommand(); c.CommandText = sql; await c.ExecuteNonQueryAsync(); }
        await Exec("""
            INSERT INTO metric_series (tenant_id, service_name, metric_name, series_id, first_seen, last_seen) VALUES
            (1, 's', 'old', 1, now64(9) - INTERVAL 200 DAY, now64(9) - INTERVAL 200 DAY),
            (1, 's', 'new', 2, now64(9), now64(9))
            """);
        await Exec("""
            INSERT INTO metric_catalog (tenant_id, service_name, metric_name, metric_type, first_seen, last_seen) VALUES
            (1, 's', 'old', 'GAUGE', now64(9) - INTERVAL 200 DAY, now64(9) - INTERVAL 200 DAY),
            (1, 's', 'new', 'GAUGE', now64(9), now64(9))
            """);

        using var scope = _db.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IRetentionSweeper>().DeleteOldMetricDataPointsAsync(Seeding.RetentionPolicies.For((1, 100)));

        foreach (var table in new[] { "metric_series", "metric_catalog" })
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT groupArray(metric_name) FROM {table}";
            var names = ((string[])(await cmd.ExecuteScalarAsync())!);
            Assert.Equal(["new"], names);
        }
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new ClickHouseConnection(_db.DatabaseConnectionString);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Statements of the lightweight deletes issued against <paramref name="table"/> since <paramref name="since"/>.</summary>
    private async Task<long> DeleteStatementsAsync(string table, DateTime since)
    {
        await ScalarAsync("SYSTEM FLUSH LOGS");
        return await ScalarAsync(
            "SELECT toInt64(count()) FROM system.query_log WHERE type = 'QueryFinish' " +
            $"AND event_time >= toDateTime({new DateTimeOffset(since).ToUnixTimeSeconds()}) " +
            $"AND query LIKE 'DELETE FROM {table} WHERE%'");
    }

    private async Task SeedSpansAsync(DateTime now, params (long Tenant, int Age)[] rows)
    {
        var spans = new List<Keryhe.Telemetry.Core.Models.SpanModel>();
        foreach (var (tenant, age) in rows)
            spans.AddRange(Seeding.SeededDataBuilder.BasicTraceWindow(tenant, now.AddDays(-age), traceCount: 3, seedOffset: (int)(tenant % 1000) * 1000 + age));
        using var scope = _db.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans);
    }

    /// <summary>Without the remembered deletes, the hourly sweep would repeat each lightweight delete every hour.</summary>
    [Fact]
    public async Task ASecondSweepOnTheSameDay_IssuesNoDelete()
    {
        var now = DateTime.UtcNow;
        await SeedSpansAsync(now, (_db.TenantId, 20), (777_001, 20), (_db.TenantId, 1));
        var policy = Seeding.RetentionPolicies.For((_db.TenantId, 10), (777_001, 60));
        var started = DateTime.UtcNow.AddSeconds(-2);

        using (var scope = _db.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRetentionSweeper>().DeleteOldTracesAsync(policy);
        var afterFirst = await DeleteStatementsAsync("spans", started);
        Assert.True(afterFirst >= 1, "the shorter window's expired days are deleted");

        using (var scope = _db.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRetentionSweeper>().DeleteOldTracesAsync(policy);
        Assert.Equal(afterFirst, await DeleteStatementsAsync("spans", started));
    }

    /// <summary>The lightweight delete reads the expired days' granules for its tenants, not the table.</summary>
    [Fact]
    public async Task TheShorterWindowDelete_ReadsOnlyTheExpiredDays()
    {
        var now = DateTime.UtcNow;
        // A lot of current data for both tenants, and a little expired data for the short-window one.
        await SeedSpansAsync(now, (_db.TenantId, 20), (_db.TenantId, 1), (777_001, 1), (777_001, 2), (777_001, 3), (777_001, 4));
        var total = await ScalarAsync("SELECT toInt64(count()) FROM spans");
        var started = DateTime.UtcNow.AddSeconds(-2);

        using (var scope = _db.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRetentionSweeper>()
                .DeleteOldTracesAsync(Seeding.RetentionPolicies.For((_db.TenantId, 10), (777_001, 60)));

        await ScalarAsync("SYSTEM FLUSH LOGS");
        var readRows = await ScalarAsync(
            "SELECT toInt64(max(read_rows)) FROM system.query_log WHERE type = 'QueryFinish' " +
            $"AND event_time >= toDateTime({new DateTimeOffset(started).ToUnixTimeSeconds()}) AND query LIKE 'DELETE FROM spans WHERE%'");
        Assert.True(readRows < total, $"the delete read {readRows} rows of {total}");
    }

    [Fact]
    public async Task SeriesAndCatalogRows_AreRemovedOnTheirTenantsWindow()
    {
        await using var conn = new ClickHouseConnection(_db.DatabaseConnectionString);
        await conn.OpenAsync();
        async Task Exec(string sql) { var c = conn.CreateCommand(); c.CommandText = sql; await c.ExecuteNonQueryAsync(); }
        // 30 days old: past tenant 1's 10 and the orphan's built-in 30 (tenant 3), inside tenant 2's 60.
        await Exec("""
            INSERT INTO metric_series (tenant_id, service_name, metric_name, series_id, first_seen, last_seen) VALUES
            (1, 's', 'a', 1, now64(9), now64(9) - INTERVAL 31 DAY),
            (2, 's', 'b', 2, now64(9), now64(9) - INTERVAL 31 DAY),
            (3, 's', 'c', 3, now64(9), now64(9) - INTERVAL 31 DAY),
            (3, 's', 'fresh', 4, now64(9), now64(9) - INTERVAL 5 DAY)
            """);
        await Exec("""
            INSERT INTO metric_catalog (tenant_id, service_name, metric_name, metric_type, first_seen, last_seen) VALUES
            (1, 's', 'a', 'GAUGE', now64(9), now64(9) - INTERVAL 31 DAY),
            (2, 's', 'b', 'GAUGE', now64(9), now64(9) - INTERVAL 31 DAY),
            (3, 's', 'c', 'GAUGE', now64(9), now64(9) - INTERVAL 31 DAY),
            (3, 's', 'fresh', 'GAUGE', now64(9), now64(9) - INTERVAL 5 DAY)
            """);

        using var scope = _db.Services.CreateScope();
        // Tenants 1 and 2 are known; tenant 3 has no control-plane row and takes the built-in 30 days.
        await scope.ServiceProvider.GetRequiredService<IRetentionSweeper>()
            .DeleteOldMetricDataPointsAsync(Seeding.RetentionPolicies.For((1, 10), (2, 60)));

        foreach (var table in new[] { "metric_series", "metric_catalog" })
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT arraySort(groupArray(metric_name)) FROM {table}";
            Assert.Equal(["b", "fresh"], (string[])(await cmd.ExecuteScalarAsync())!);
        }
    }
}
