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
        await scope.ServiceProvider.GetRequiredService<IRetentionSweeper>().DeleteOldMetricDataPointsAsync(TimeSpan.FromDays(100));

        foreach (var table in new[] { "metric_series", "metric_catalog" })
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT groupArray(metric_name) FROM {table}";
            var names = ((string[])(await cmd.ExecuteScalarAsync())!);
            Assert.Equal(["new"], names);
        }
    }
}
