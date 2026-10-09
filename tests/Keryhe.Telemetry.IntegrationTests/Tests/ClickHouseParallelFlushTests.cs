using ClickHouse.Client.ADO;
using Dapper;
using Keryhe.Telemetry.ClickHouse.Services;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// A large batch is built on several threads and inserted as several concurrent INSERTs, each under its own token; a small one
/// is a single INSERT as before. The split must store exactly what the single insert stores.
/// </summary>
[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseParallelFlushTests(ClickHouseFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private const int Rows = 1_000;

    private ClickHouseBulkWriter NewWriter(bool split) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Collector"] = fixture.DatabaseConnectionString }).Build(),
        NullLogger<ClickHouseBulkWriter>.Instance,
        Options.Create(new ClickHouseIngestionOptions
        {
            ParallelFlushMinRows = split ? 200 : 1_000_000, InsertPieceRows = 100, MaxParallelInserts = 4
        }));

    // Eleven resources/scopes shared across many rows: the memo in FlushContext is hit from several threads at once.
    private ResourceModel[] Resources => Enumerable.Range(0, 11).Select(i => new ResourceModel
    {
        TenantId = fixture.TenantId,
        SchemaUrl = "https://schemas/r",
        Attributes = new() { ["service.name"] = $"svc-{i}", ["r"] = $"res-{i}", ["n"] = (long)i }
    }).ToArray();

    private InstrumentationScopeModel[] Scopes => Enumerable.Range(0, 11).Select(i => new InstrumentationScopeModel
    {
        Name = $"scope-{i}", Version = "1", Attributes = new() { ["s"] = $"sc-{i}" }
    }).ToArray();

    private List<SpanModel> Spans()
    {
        var resources = Resources; var scopes = Scopes;
        return Enumerable.Range(0, Rows).Select(i => new SpanModel
        {
            TraceIdHex = (i + 1).ToString("x32"), SpanIdHex = (i + 1).ToString("x16"), Name = $"op-{i % 7}", Kind = SpanKind.SERVER,
            StartTimeUnixNano = SeededDataBuilder.ToUnixNano(T0) + i * 1_000_003L, EndTimeUnixNano = SeededDataBuilder.ToUnixNano(T0) + i * 1_000_003L + 5_000 + i,
            StatusCode = i % 5 == 0 ? SpanStatusCode.ERROR : SpanStatusCode.OK,
            Attributes = new() { ["i"] = (long)i, ["s"] = $"v{i % 13}" },
            Events = [new SpanEventModel { Name = "e", TimeUnixNano = SeededDataBuilder.ToUnixNano(T0) + i }],
            Resource = resources[i % 11], InstrumentationScope = scopes[i % 11]
        }).ToList();
    }

    private List<LogRecordModel> Logs()
    {
        var resources = Resources; var scopes = Scopes;
        return Enumerable.Range(0, Rows).Select(i => new LogRecordModel
        {
            TimeUnixNano = SeededDataBuilder.ToUnixNano(T0) + i * 1_000_003L, SeverityNumber = 9 + i % 4, BodyValue = $"body {i}",
            Attributes = new() { ["i"] = (long)i }, Resource = resources[i % 11], InstrumentationScope = scopes[i % 11]
        }).ToList();
    }

    private List<MetricModel> Metrics()
    {
        var resources = Resources; var scopes = Scopes;
        var sums = Enumerable.Range(0, Rows / 10).Select(m => new MetricModel
        {
            Name = $"sum-{m % 5}", Type = MetricType.SUM, Unit = "1", Resource = resources[m % 11], InstrumentationScope = scopes[m % 11],
            SumDataPoints = Enumerable.Range(0, 10).Select(p => new SumDataPointModel
            {
                TimeUnixNano = SeededDataBuilder.ToUnixNano(T0) + (m * 10 + p) * 1_000_003L, ValueDouble = m * 10 + p, IsMonotonic = true,
                AggregationTemporality = AggregationTemporality.CUMULATIVE, Attributes = new() { ["p"] = (long)p }
            }).ToList()
        });
        return sums.Concat(Enumerable.Range(0, Rows / 10).Select(m => new MetricModel
        {
            Name = $"metric-{m % 5}", Type = MetricType.GAUGE, Unit = "1", Resource = resources[m % 11], InstrumentationScope = scopes[m % 11],
            GaugeDataPoints = Enumerable.Range(0, 10).Select(p => new GaugeDataPointModel
            {
                TimeUnixNano = SeededDataBuilder.ToUnixNano(T0) + (m * 10 + p) * 1_000_003L, ValueDouble = m * 10 + p, Attributes = new() { ["p"] = (long)p }
            }).ToList()
        })).ToList();
    }

    private async Task<List<dynamic>> QueryAsync(string sql)
    {
        await using var conn = new ClickHouseConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        return (await conn.QueryAsync<dynamic>(sql)).ToList();
    }

    private async Task<(long Count, string Checksum)> FingerprintAsync(string table, string columns)
    {
        var r = (await QueryAsync($"SELECT count() AS c, toString(sum(cityHash64({columns}))) AS h FROM {table}"))[0];
        return (Convert.ToInt64(r.c), (string)r.h);
    }

    private const string SpanColumns = "tenant_id, service_name, span_name, start_time, duration_ns, trace_id, span_id, status_code, toString(attributes), toString(resource_attributes), toString(scope_attributes), scope_name, toString(`events.name`)";
    private const string LogColumns = "tenant_id, service_name, timestamp, severity_number, body, toString(attributes), toString(resource_attributes), toString(scope_attributes), scope_name";
    private const string SumColumns = "tenant_id, service_name, metric_name, series_id, time, value, toString(attributes), is_monotonic, temporality";
    private const string PointColumns = "tenant_id, service_name, metric_name, series_id, time, value, toString(attributes), toString(resource_attributes), scope_name";

    private const string CatalogColumns = "tenant_id, service_name, metric_name, metric_type, unit, toString(first_seen), toString(last_seen)";
    private const string SeriesColumns = "tenant_id, service_name, metric_name, series_id, toString(attributes), toString(resource_attributes), scope_name, toString(first_seen), toString(last_seen)";

    private async Task<long> InsertsAsync(string table, string since)
    {
        await QueryAsync("SYSTEM FLUSH LOGS");
        return Convert.ToInt64((await QueryAsync(
            $"SELECT count() AS c FROM system.query_log WHERE type = 'QueryFinish' AND query_kind = 'Insert' AND has(tables, 'telemetry.{table}') " +
            $"AND event_time_microseconds >= toDateTime64('{since}', 6)"))[0].c);
    }

    private async Task<string> NowAsync() => (string)(await QueryAsync("SELECT toString(now64(6)) AS t"))[0].t;

    [Fact]
    public async Task SplitBatches_StoreExactlyWhatASingleInsertStores_ForSpansLogsAndPoints()
    {
        await using (var single = NewWriter(split: false))
        {
            await single.FlushTracesAsync(Spans());
            await single.FlushLogsAsync(Logs());
            await single.FlushMetricsAsync(Metrics());
        }
        var spans1 = await FingerprintAsync("spans", SpanColumns);
        var logs1 = await FingerprintAsync("log_records", LogColumns);
        var points1 = await FingerprintAsync("gauge_points", PointColumns);
        var sums1 = await FingerprintAsync("sum_points", SumColumns);
        Assert.Equal(Rows, sums1.Count);
        var catalog1 = await FingerprintAsync("metric_catalog", CatalogColumns);
        var series1 = await FingerprintAsync("metric_series", SeriesColumns);
        Assert.True(catalog1.Count > 0 && series1.Count > 0);
        var rollup1 = await FingerprintAsync("request_rollup_minute", "tenant_id, service_name, bucket_start_unix_nano, operation, request_count, error_count, sum_duration_nanos");
        Assert.Equal(Rows, spans1.Count);
        Assert.Equal(Rows, logs1.Count);
        Assert.Equal(Rows, points1.Count);

        await fixture.ResetAsync();
        var since = await NowAsync();
        await using (var split = NewWriter(split: true))
        {
            await split.FlushTracesAsync(Spans());
            await split.FlushLogsAsync(Logs());
            await split.FlushMetricsAsync(Metrics());
        }
        Assert.Equal(spans1, await FingerprintAsync("spans", SpanColumns));
        Assert.Equal(logs1, await FingerprintAsync("log_records", LogColumns));
        Assert.Equal(points1, await FingerprintAsync("gauge_points", PointColumns));
        Assert.Equal(sums1, await FingerprintAsync("sum_points", SumColumns));
        Assert.Equal(catalog1, await FingerprintAsync("metric_catalog", CatalogColumns));
        Assert.Equal(series1, await FingerprintAsync("metric_series", SeriesColumns));
        Assert.Equal(rollup1, await FingerprintAsync("request_rollup_minute", "tenant_id, service_name, bucket_start_unix_nano, operation, request_count, error_count, sum_duration_nanos"));

        // 1,000 rows, pieces of at least 100, at most 4: four INSERTs per raw table. The derived table stays small (one insert).
        Assert.Equal(4, await InsertsAsync("spans", since));
        Assert.Equal(4, await InsertsAsync("log_records", since));
        Assert.Equal(4, await InsertsAsync("gauge_points", since));
    }

    [Fact]
    public async Task ASmallBatch_IsOneInsert_EvenWithSplittingEnabled()
    {
        var since = await NowAsync();
        await using var split = NewWriter(split: true);
        await split.FlushTracesAsync(Spans().Take(150).ToList());
        Assert.Equal(150, (await FingerprintAsync("spans", SpanColumns)).Count);
        Assert.Equal(1, await InsertsAsync("spans", since));
    }

    [Fact]
    public async Task ARetryOfASplitBatchWithTheSameToken_StoresEveryPieceOnce()
    {
        await using var split = NewWriter(split: true);
        var spans = Spans();
        // Four pieces under four distinct tokens all land (a shared token would drop three of them); a re-send of the identical
        // batch and token drops each piece again.
        await split.FlushTracesAsync(spans, "retry-token");
        Assert.Equal(Rows, (await FingerprintAsync("spans", SpanColumns)).Count);
        await split.FlushTracesAsync(spans, "retry-token");
        Assert.Equal(Rows, (await FingerprintAsync("spans", SpanColumns)).Count);

        await split.FlushTracesAsync(spans, "another-token");
        Assert.Equal(2 * Rows, (await FingerprintAsync("spans", SpanColumns)).Count);
    }
}
