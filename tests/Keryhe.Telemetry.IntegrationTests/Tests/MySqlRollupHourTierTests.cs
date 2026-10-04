using Dapper;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// MySQL's rollup hour tier (plans/summary-rollups.md, Phase 4; built because the 7-day request summary missed the 1 s gate
/// there): compaction equals the minute rows summed, late rows fold in inside <c>RecompactHours</c> and not beyond it, a
/// second compactor cannot run at once, reads across the compacted boundary equal minute-only reads, and the hour tier
/// really is what answers for compacted hours.
/// </summary>
[Collection(ProviderNames.MySql)]
[Trait("Provider", ProviderNames.MySql)]
public sealed class MySqlRollupHourTierTests(MySqlFixture fixture) : IAsyncLifetime
{
    private const long Minute = 60_000_000_000L;
    private const long Hour = 3_600_000_000_000L;
    private static readonly string[] Services = ["checkout-api", "payments-worker", "inventory-api"];

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using var conn = await OpenAsync();
        foreach (var table in new[] { "request_rollup_hour", "log_rollup_hour", "rollup_compaction" })
            await conn.ExecuteAsync($"DELETE FROM {table}");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<MySqlConnection> OpenAsync()
    {
        var conn = new MySqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static long FloorHour(long nanos) => nanos - nanos % Hour;
    private static long Now() => TimeConversion.DateTimeToUnixNano(DateTime.UtcNow);

    private static RequestRollupRow Req(long tenant, string service, long minute, int seed)
    {
        var row = new RequestRollupRow
        {
            TenantId = tenant, ServiceName = service, BucketStartUnixNano = minute,
            RequestCount = 4 + seed % 5, ErrorCount = seed % 2, SumDurationNanos = 7_000_000L * (1 + seed % 3),
            MaxDurationNanos = 3_000_000L * (1 + seed % 4)
        };
        row.Bands[seed % DurationBands.Count] = row.RequestCount;
        return row;
    }

    /// <summary>Minute rows for [firstHour, lastHour) at every fifth minute, two partial rows per key.</summary>
    private async Task<(List<RequestRollupRow> Requests, List<LogRollupRow> Logs)> SeedMinuteRowsAsync(long firstHour, long lastHour)
    {
        var requests = new List<RequestRollupRow>();
        var logs = new List<LogRollupRow>();
        var seed = 0;
        for (var minute = firstHour; minute < lastHour; minute += 5 * Minute)
            foreach (var service in Services)
                for (var partial = 0; partial < 2; partial++)
                {
                    seed++;
                    requests.Add(Req(fixture.TenantId, service, minute, seed));
                    foreach (var severity in new[] { -1, 9, 17 })
                        logs.Add(new LogRollupRow { TenantId = fixture.TenantId, ServiceName = service, SeverityNumber = severity, BucketStartUnixNano = minute, RecordCount = 1 + seed % 7 });
                }
        using var scope = fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRollupStore>();
        await store.AppendRequestsAsync(requests, default);
        await store.AppendLogsAsync(logs, default);
        return (requests, logs);
    }

    private async Task<Dictionary<(string Service, long Hour), long[]>> HourRequestsAsync()
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            "SELECT service_name, bucket_start_unix_nano, request_count, error_count, sum_duration_nanos, max_duration_nanos FROM request_rollup_hour WHERE tenant_id = @t",
            new { t = fixture.TenantId });
        return rows.ToDictionary(r => ((string)r.service_name, (long)r.bucket_start_unix_nano),
            r => new[] { (long)r.request_count, (long)r.error_count, (long)r.sum_duration_nanos, (long)r.max_duration_nanos });
    }

    private async Task CompactAsync(DateTime? now = null)
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IRollupCompactor>().CompactAsync(now ?? DateTime.UtcNow);
    }

    [Fact]
    public async Task Compaction_EqualsTheMinuteRowsSummed_ForClosedHoursOnly_AndRecordsTheBoundary()
    {
        var thisHour = FloorHour(Now());
        var (requests, logs) = await SeedMinuteRowsAsync(thisHour - 8 * Hour, thisHour + Hour);
        await CompactAsync();

        var writtenThrough = TimeConversion.DateTimeToUnixNano(RollupSummaryBuilder.WrittenThrough(DateTime.UtcNow, new RollupOptions()));
        var closedTo = FloorHour(writtenThrough);

        var expected = requests.Where(r => r.BucketStartUnixNano < closedTo)
            .GroupBy(r => (r.ServiceName, FloorHour(r.BucketStartUnixNano)))
            .ToDictionary(g => g.Key, g => new[] { g.Sum(r => r.RequestCount), g.Sum(r => r.ErrorCount), g.Sum(r => r.SumDurationNanos), g.Max(r => r.MaxDurationNanos) });
        var actual = await HourRequestsAsync();
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Keys.OrderBy(k => k).ToList(), actual.Keys.OrderBy(k => k).ToList());
        foreach (var (key, e) in expected) Assert.True(e.SequenceEqual(actual[key]), $"{key}");

        await using var conn = await OpenAsync();
        var logRows = await conn.QueryAsync<(string Service, int Severity, long Hour, long Count)>(
            "SELECT service_name, severity_number, bucket_start_unix_nano, record_count FROM log_rollup_hour WHERE tenant_id = @t", new { t = fixture.TenantId });
        var expectedLogs = logs.Where(l => l.BucketStartUnixNano < closedTo)
            .GroupBy(l => (l.ServiceName, l.SeverityNumber, FloorHour(l.BucketStartUnixNano)))
            .ToDictionary(g => g.Key, g => g.Sum(l => l.RecordCount));
        Assert.Equal(expectedLogs.OrderBy(k => k.Key).ToList(),
            logRows.ToDictionary(r => (r.Service, r.Severity, r.Hour), r => r.Count).OrderBy(k => k.Key).ToList());

        foreach (var signal in new[] { "request", "log" })
            Assert.Equal(closedTo, await conn.ExecuteScalarAsync<long>("SELECT compacted_through_unix_nano FROM rollup_compaction WHERE signal_kind = @signal", new { signal }));
    }

    [Fact]
    public async Task LateRows_FoldInInsideRecompactHours_AndNotBeyondIt()
    {
        var thisHour = FloorHour(Now());
        await SeedMinuteRowsAsync(thisHour - 10 * Hour, thisHour);
        await CompactAsync();
        var before = await HourRequestsAsync();

        // A late minute row for an hour 2 h back (inside the default 6 h) and one 9 h back (outside it).
        var recent = thisHour - 2 * Hour + 3 * Minute;
        var old = thisHour - 9 * Hour + 3 * Minute;
        var lateRecent = Req(fixture.TenantId, "checkout-api", recent, 3);
        var lateOld = Req(fixture.TenantId, "checkout-api", old, 3);
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRollupStore>().AppendRequestsAsync([lateRecent, lateOld], default);

        await CompactAsync();
        var after = await HourRequestsAsync();

        var recentKey = ("checkout-api", FloorHour(recent));
        var oldKey = ("checkout-api", FloorHour(old));
        Assert.Equal(before[recentKey][0] + lateRecent.RequestCount, after[recentKey][0]);   // folded in
        Assert.Equal(before[oldKey][0], after[oldKey][0]);                                     // beyond RecompactHours: stays minute-only
    }

    [Fact]
    public async Task ACompactionWhileAnotherHoldsTheLock_DoesNothing()
    {
        var thisHour = FloorHour(Now());
        await SeedMinuteRowsAsync(thisHour - 4 * Hour, thisHour);

        await using var holder = await OpenAsync();
        Assert.Equal(1, await holder.ExecuteScalarAsync<int>("SELECT GET_LOCK('keryhe_rollup_compaction', 0)"));

        using var scope = fixture.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IRollupCompactor>().CompactAsync(DateTime.UtcNow));
        Assert.Empty(await HourRequestsAsync());

        await holder.ExecuteScalarAsync("SELECT RELEASE_LOCK('keryhe_rollup_compaction')");
        Assert.True(await scope.ServiceProvider.GetRequiredService<IRollupCompactor>().CompactAsync(DateTime.UtcNow) > 0);
        Assert.NotEmpty(await HourRequestsAsync());
    }

    [Fact]
    public async Task ConcurrentCompactions_NeverDoubleAnHour()
    {
        var thisHour = FloorHour(Now());
        var (requests, _) = await SeedMinuteRowsAsync(thisHour - 6 * Hour, thisHour);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CompactAsync()));

        var actual = await HourRequestsAsync();
        var expected = requests.GroupBy(r => (r.ServiceName, FloorHour(r.BucketStartUnixNano))).ToDictionary(g => g.Key, g => g.Sum(r => r.RequestCount));
        Assert.NotEmpty(actual);
        foreach (var (key, count) in expected.Where(e => actual.ContainsKey(e.Key)))
            Assert.Equal(count, actual[key][0]);
    }

    [Theory]
    [InlineData(3600, null)]
    [InlineData(10800, null)]
    [InlineData(7200, "checkout-api")]
    public async Task ReadsAcrossTheCompactedBoundary_EqualMinuteOnlyReads(int bucketSeconds, string? service)
    {
        var thisHour = FloorHour(Now());
        var (requests, logs) = await SeedMinuteRowsAsync(thisHour - 12 * Hour, thisHour + Hour);
        await CompactAsync();

        // Starts and ends mid-hour, so the edges are partial hours read from the minute rows; the middle is hour tier up
        // to the boundary and minute rows after it.
        var query = new RollupQuery
        {
            StartNano = thisHour - 10 * Hour + 17 * Minute, EndNano = thisHour + Hour - 8 * Minute,
            BucketSeconds = bucketSeconds, Service = service, MinSeverity = null
        };
        var width = bucketSeconds * 1_000_000_000L;
        var expected = requests
            .Where(r => r.BucketStartUnixNano >= query.StartNano && r.BucketStartUnixNano < query.EndNano && (service == null || r.ServiceName == service))
            .GroupBy(r => (r.BucketStartUnixNano / width * width, r.ServiceName))
            .ToDictionary(g => g.Key, g => (Count: g.Sum(r => r.RequestCount), Sum: g.Sum(r => r.SumDurationNanos), Max: g.Max(r => r.MaxDurationNanos)));

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRollupReadRepository>();
        var actual = (await repo.GetRequestRollupAsync(query)).Rows.ToDictionary(r => (r.BucketStartNano, r.Service));
        Assert.Equal(expected.Keys.OrderBy(k => k).ToList(), actual.Keys.OrderBy(k => k).ToList());
        foreach (var (key, e) in expected)
        {
            Assert.Equal(e.Count, actual[key].RequestCount);
            Assert.Equal(e.Sum, actual[key].SumDurationNanos);
            Assert.Equal(e.Max, actual[key].MaxDurationNanos);
        }

        var logQuery = new RollupQuery { StartNano = query.StartNano, EndNano = query.EndNano, BucketSeconds = bucketSeconds, Service = service, MinSeverity = 9 };
        var expectedLogs = logs
            .Where(l => l.BucketStartUnixNano >= query.StartNano && l.BucketStartUnixNano < query.EndNano && (service == null || l.ServiceName == service) && l.SeverityNumber >= 9)
            .GroupBy(l => (l.BucketStartUnixNano / width * width, l.SeverityNumber))
            .ToDictionary(g => g.Key, g => g.Sum(l => l.RecordCount));
        var actualLogs = (await repo.GetLogRollupAsync(logQuery)).Rows.ToDictionary(r => (r.BucketStartNano, r.SeverityNumber), r => r.RecordCount);
        Assert.Equal(expectedLogs.OrderBy(k => k.Key).ToList(), actualLogs.OrderBy(k => k.Key).ToList());
    }

    [Fact]
    public async Task TheHourTierIsWhatAnswersForCompactedHours()
    {
        var thisHour = FloorHour(Now());
        await SeedMinuteRowsAsync(thisHour - 8 * Hour, thisHour);
        await CompactAsync();

        var target = thisHour - 7 * Hour;
        var query = new RollupQuery { StartNano = target, EndNano = target + Hour, BucketSeconds = 3600 };
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRollupReadRepository>();
        var viaTier = (await repo.GetRequestRollupAsync(query)).Rows.Sum(r => r.RequestCount);
        Assert.True(viaTier > 0);

        // Remove that hour's minute rows (as retention would): the hour tier still answers; a minute-width read does not.
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync("DELETE FROM request_rollup_minute WHERE bucket_start_unix_nano >= @a AND bucket_start_unix_nano < @b", new { a = target, b = target + Hour });
        Assert.Equal(viaTier, (await repo.GetRequestRollupAsync(query)).Rows.Sum(r => r.RequestCount));
        Assert.Equal(0, (await repo.GetRequestRollupAsync(new RollupQuery { StartNano = target, EndNano = target + Hour, BucketSeconds = 60 })).Rows.Sum(r => r.RequestCount));
    }

    [Fact]
    public async Task Retention_AlsoSweepsTheHourTier()
    {
        var old = FloorHour(Now()) - 100 * 24 * Hour;
        var current = FloorHour(Now()) - 3 * Hour;
        using (var scope = fixture.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IRollupStore>();
            await store.AppendRequestsAsync([Req(fixture.TenantId, "checkout-api", old, 1), Req(fixture.TenantId, "checkout-api", current, 2)], default);
            await store.AppendLogsAsync([new LogRollupRow { TenantId = fixture.TenantId, ServiceName = "checkout-api", SeverityNumber = 9, BucketStartUnixNano = old, RecordCount = 1 },
                                          new LogRollupRow { TenantId = fixture.TenantId, ServiceName = "checkout-api", SeverityNumber = 9, BucketStartUnixNano = current, RecordCount = 1 }], default);
        }
        await CompactAsync();
        Assert.Equal(2, (await HourRequestsAsync()).Count);

        using (var scope = fixture.Services.CreateScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>();
            await retention.DeleteOldTracesAsync(TimeSpan.FromDays(90));
            await retention.DeleteOldLogRecordsAsync(TimeSpan.FromDays(90));
        }

        Assert.Equal([FloorHour(current)], (await HourRequestsAsync()).Keys.Select(k => k.Hour).ToList());
        await using var conn = await OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM log_rollup_hour WHERE tenant_id = @t", new { t = fixture.TenantId }));
    }
}
