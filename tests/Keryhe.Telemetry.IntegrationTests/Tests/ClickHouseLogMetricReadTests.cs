using ClickHouse.Client.ADO;
using Dapper;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// ClickHouse-only log and metric read behaviour on top of the shared bases (plans/clickhouse-redesign phase 5): how many rows a
/// logs list and a chart read (the sort-key rules of Phase 0 spike 5), the word-versus-substring search forms, labels, exemplars,
/// the catalog and the service list.
/// </summary>
[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseLogMetricReadTests(ClickHouseFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime Day = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);

    private ClickHouseConnection Open()
    {
        var conn = new ClickHouseConnection(fixture.DatabaseConnectionString);
        conn.Open();
        return conn;
    }

    private async Task<long> ReadRowsSinceAsync(long sinceUnixSeconds, string table)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("SYSTEM FLUSH LOGS");
        return await conn.ExecuteScalarAsync<long>(
            "SELECT toInt64(sum(read_rows)) FROM system.query_log WHERE type = 'QueryFinish' AND event_time_microseconds >= fromUnixTimestamp64Micro(@since) " +
            "AND query_kind = 'Select' AND query LIKE '%FROM " + table + "%' AND query NOT LIKE '%system.query_log%'", new { since = sinceUnixSeconds });
    }

    private async Task<string> DescribeAsync(long since)
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync<(long, string)>("SELECT toInt64(read_rows), substring(query, 1, 160) FROM system.query_log WHERE type = 'QueryFinish' AND event_time_microseconds >= fromUnixTimestamp64Micro(@since) AND query_kind = 'Select' AND query LIKE '%log_records%'", new { since });
        return string.Join(" | ", rows.Select(r => $"{r.Item1}: {r.Item2}"));
    }

    /// <summary>The database's own clock, so a read is measured from this instant whatever the container's clock does.</summary>
    private async Task<long> NowSeconds()
    {
        await using var conn = Open();
        return await conn.ExecuteScalarAsync<long>("SELECT toUnixTimestamp64Micro(now64(6))");
    }

    private List<LogRecordModel> BigLogDay(int count)
    {
        var resourceA = SeededDataBuilder.Resource(fixture.TenantId, "svc-a");
        var resourceB = SeededDataBuilder.Resource(fixture.TenantId, "svc-b");
        var scope = SeededDataBuilder.Scope();
        var logs = new List<LogRecordModel>(count);
        for (var i = 0; i < count; i++)
        {
            var time = SeededDataBuilder.ToUnixNano(Day) + (long)(86_400L * 1_000_000_000L / count) * i;
            logs.Add(new LogRecordModel
            {
                TimeUnixNano = time, ObservedTimeUnixNano = time, SeverityNumber = i % 20 == 0 ? 17 : 9,
                BodyValue = i % 1000 == 0 ? $"request failed with rareword{i / 1000} at {i}" : $"request {i} handled ok",
                Resource = i % 2 == 0 ? resourceA : resourceB, InstrumentationScope = scope,
                Attributes = new() { ["http.status_code"] = i % 20 == 0 ? 500L : 200L }
            });
        }
        return logs;
    }

    [Fact]
    public async Task ALogsList_ReadsRowsInProportionToTheRequestedRows_NotTheWindow_AndAnHourReadsAnHour()
    {
        const int count = 240_000;
        await fixture.Services.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(BigLogDay(count));

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        // newest 50 of a whole day: a few granules, not the day
        var since = await NowSeconds();
        var newest = await repo.GetLogListAsync(new LogQuery { Start = Day, End = Day.AddDays(1), Limit = 50 });
        Assert.Equal(50, newest.Items.Count);
        Assert.True(newest.Truncated);
        var read = await ReadRowsSinceAsync(since, "log_records");
        Assert.True(read < count / 4, $"the newest 50 of a day read {read} of {count} rows: {await DescribeAsync(since)}");

        // oldest first, one service, with a severity floor: still bounded by what is asked for
        since = await NowSeconds();
        var filtered = await repo.GetLogListAsync(new LogQuery { Start = Day, End = Day.AddDays(1), Limit = 50, Service = "svc-a", MinSeverity = 17, Order = ListOrder.Oldest });
        Assert.Equal(50, filtered.Items.Count);
        Assert.All(filtered.Items, l => Assert.True(l.SeverityNumber >= 17));
        read = await ReadRowsSinceAsync(since, "log_records");
        Assert.True(read < count / 2, $"a filtered oldest-first list read {read} of {count} rows");

        // a one-hour window reads about an hour (the bucket predicate), not the day
        since = await NowSeconds();
        var hour = await repo.GetLogListAsync(new LogQuery { Start = Day.AddHours(10), End = Day.AddHours(11), Limit = 1000, Search = "rareword105" });
        Assert.Single(hour.Items); // i = 105,000 is at 10.5 h
        read = await ReadRowsSinceAsync(since, "log_records");
        Assert.True(read < count / 6, $"a one-hour word search read {read} of {count} rows (an hour is {count / 24})");
    }

    [Fact]
    public async Task WholeWordSearchUsesTheTokenIndex_AndSubstringSearchStillFindsPartsOfWords()
    {
        await fixture.Services.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(BigLogDay(20_000));
        await using (var conn = Open())
        {
            var plan = string.Join("\n", await conn.QueryAsync<string>(
                "EXPLAIN indexes = 1 SELECT count() FROM log_records WHERE hasToken(lower(body), 'rareword3')"));
            Assert.Contains("idx_body", plan);
            var scan = string.Join("\n", await conn.QueryAsync<string>(
                "EXPLAIN indexes = 1 SELECT count() FROM log_records WHERE positionCaseInsensitiveUTF8(body, 'areword3') > 0"));
            Assert.DoesNotContain("idx_body", scan);
        }

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ILogReadRepository>();
        var word = await repo.GetLogListAsync(new LogQuery { Start = Day, End = Day.AddDays(1), Search = "RAREWORD3", Limit = 100 });
        var part = await repo.GetLogListAsync(new LogQuery { Start = Day, End = Day.AddDays(1), Search = "areword-or-so", Limit = 100 });
        Assert.Equal(1, word.Items.Count);               // i = 3000 in a 20,000-row day
        Assert.Empty(part.Items);                          // not a word, not present
        var substring = await repo.GetLogListAsync(new LogQuery { Start = Day, End = Day.AddDays(1), Search = "failed with rareword1 at", Limit = 100 });
        Assert.Equal(1, substring.Items.Count);
    }

    [Fact]
    public async Task ALogsList_FacetsByTraceAndSurrounding_ReturnTheRightRows()
    {
        var logs = BigLogDay(2_000);
        logs[500].TraceIdHex = new string('a', 32);
        logs[500].SpanIdHex = new string('b', 16);
        await fixture.Services.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(logs);

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        var byTrace = (await repo.GetLogRecordsByTraceIdAsync(new string('a', 32))).Single();
        Assert.Equal((new string('b', 16), 500L), (byTrace.SpanIdHex, logs.IndexOf(logs.Single(l => l.TraceIdHex != null))));

        var around = (await repo.GetSurroundingLogRecordsAsync(logs[500].TimeUnixNano!.Value, null, before: 3, after: 3)).ToList();
        Assert.Equal(logs.Skip(497).Take(7).Select(l => l.TimeUnixNano), around.Select(l => l.TimeUnixNano));

        var facets = await repo.GetLogFacetsAsync(new LogFacetsQuery { Start = Day, End = Day.AddDays(1), Keys = ["http.status_code"] });
        var status = Assert.Single(facets.Facets);
        Assert.Equal(["200", "500"], status.Values.Select(v => v.Value).Order().ToArray());
        Assert.Equal(2_000, facets.SampleSize);
        Assert.Equal(100, status.Values.Single(v => v.Value == "500").Count);
    }

    private MetricModel Gauge(string service, string pod, DateTime start, int points, string name = "chart.cpu") => new()
    {
        Name = name, Type = MetricType.GAUGE, Unit = "1", Description = "cpu", InstrumentationScope = SeededDataBuilder.Scope(),
        Resource = SeededDataBuilder.Resource(fixture.TenantId, service, instanceId: pod),
        GaugeDataPoints = Enumerable.Range(0, points).Select(i => new GaugeDataPointModel
        {
            TimeUnixNano = SeededDataBuilder.ToUnixNano(start.AddMinutes(i)), ValueDouble = i, Attributes = new() { ["pod"] = pod, ["zone"] = pod.EndsWith('1') ? "a" : "b" }
        }).ToList()
    };

    [Fact]
    public async Task AChartReadsTheRowsOfItsWindow_Labels_Catalog_AndServices_Work()
    {
        // 60 series x 1,440 one-minute points over a day
        var metrics = Enumerable.Range(0, 60).Select(p => Gauge("chart-svc", $"pod-{p}", Day, 1440)).ToList();
        await fixture.Services.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync(metrics);

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var since = await NowSeconds();
        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery { MetricName = "chart.cpu", Start = Day.AddHours(5), End = Day.AddHours(6), Points = 60, Top = 100 });
        Assert.NotNull(result);
        Assert.Equal(60, result!.Series.Count);                                // a stream per pod, labelled from metric_series
        Assert.All(result.Series, s => Assert.True(s.Labels.ContainsKey("pod")));
        var rows = await ReadRowsSinceAsync(since, "gauge_points");
        Assert.True(rows < 60 * 1440 / 3, $"an hour of 60 series read {rows} rows (matching: {60 * 60}, table: {60 * 1440})");

        var oneService = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "chart.cpu", Start = Day.AddHours(5), End = Day.AddHours(6), Points = 60, Top = 100, LabelFilters = new() { ["pod"] = "pod-7" }
        });
        var only = Assert.Single(oneService!.Series);
        Assert.Equal("chart-svc", only.ServiceName);

        var labels = await repo.GetMetricLabelsAsync("chart.cpu", Day, Day.AddDays(1));
        Assert.Equal(60, labels.Labels["pod"].Count);
        Assert.Equal(["a", "b"], labels.Labels["zone"].ToArray());

        var info = Assert.Single(await repo.GetMetricsByNameAsync("chart.cpu"));
        Assert.Equal(60 * 1440, info.DataPointCount);
        Assert.Equal(Day, info.FirstSeen);
        Assert.Contains("chart.cpu", await repo.GetUniqueMetricNamesAsync("chart-svc"));
        Assert.Single(await repo.GetMetricsByTypeAsync(MetricType.GAUGE));
        Assert.Equal(1, (await repo.GetMetricCountsByTypeAsync())["GAUGE"]);
        Assert.Equal(1439, (await repo.GetLatestMetricValuesAsync("chart-svc"))["chart.cpu"]);
        Assert.NotNull(await repo.GetMetricByIdAsync(info.Id));
        Assert.Null(await repo.GetMetricByIdAsync(info.Id + 1));

        var services = await scope.ServiceProvider.GetRequiredService<IResourceReadRepository>().GetDistinctServicesAsync();
        Assert.Equal(["chart-svc"], services);
    }

    [Fact]
    public async Task ExemplarsKeepTheirTraceSpanValueAndFilteredAttributes_AndTheSeriesNameTheirPoint()
    {
        var metric = SeededDataBuilder.GaugeWithExemplars(fixture.TenantId, Day, points: 5);
        metric.GaugeDataPoints![2].Exemplars![0].SpanIdHex = "00000000000000aa";
        metric.GaugeDataPoints[2].Exemplars![0].FilteredAttributes = new() { ["user"] = "u1" };
        await fixture.Services.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([metric]);

        using var scope = fixture.Services.CreateScope();
        var page = await scope.ServiceProvider.GetRequiredService<IMetricReadRepository>().GetMetricExemplarsAsync(new MetricExemplarQuery
        {
            MetricName = "phase4.exemplar.gauge", Start = Day.AddMinutes(-1), End = Day.AddMinutes(1), Limit = 10
        });
        Assert.Equal(5, page!.Exemplars.Count);
        var withSpan = Assert.Single(page.Exemplars, e => e.Exemplar.SpanIdHex != null);
        Assert.Equal("00000000000000aa", withSpan.Exemplar.SpanIdHex);
        Assert.Equal("u1", withSpan.Exemplar.FilteredAttributes!["user"]);
        Assert.Equal(2d, withSpan.Exemplar.ValueDouble);
        Assert.Equal(2d, withSpan.PointDoubleValue);
        Assert.Equal("/checkout", withSpan.Labels["route"]);
        Assert.Contains("exemplar-svc", withSpan.SeriesName);
    }
}
