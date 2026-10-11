using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Keryhe.Telemetry.Core.Models;
using static Keryhe.Telemetry.Parquet.Tests.Spans;

namespace Keryhe.Telemetry.Parquet.Tests;

/// <summary>The log and metric tables (plans/parquet-duckdb phase 5): what the writer puts in each file, and that compaction keeps it.</summary>
public class LogMetricWriteTests
{
    private static LogRecordModel Log(ResourceModel r, long? time, int? severity, string body, string? trace = null, string? span = null)
        => new()
        {
            Resource = r,
            InstrumentationScope = new InstrumentationScopeModel { Name = "scope", Version = "1" },
            TimeUnixNano = time,
            SeverityNumber = severity,
            BodyValue = body,
            TraceIdHex = trace,
            SpanIdHex = span,
            Attributes = new Dictionary<string, object> { ["k"] = "v", ["n"] = 5 }
        };

    private static MetricModel Metric(ResourceModel r, string name, MetricType type) => new()
    {
        Name = name, Unit = "ms", Description = "d", Type = type, Resource = r,
        InstrumentationScope = new InstrumentationScopeModel { Name = "scope", Version = "1" }
    };

    private static async Task<long> RowsAsync(ParquetFixture fx, string table, string select = "count(*)", string where = "true")
    {
        var hours = fx.Store.EnumeratePartitions(table, 1, DateOnly.MinValue, DateOnly.MaxValue).ToList();
        var files = (await ParquetManifests.ResolveAsync(fx.Store, hours, default)).Select(f => f.PathIn(fx.Store)).ToList();
        return files.Count == 0 ? 0 : ParquetFixture.Scalar<long>(files, $"CAST({select} AS BIGINT)", where);
    }

    [Fact]
    public async Task Logs_AreWrittenPerHour_WithTraceIdsAndTheFallbackTime_AndARollupWithMinusOneForNoSeverity()
    {
        using var fx = new ParquetFixture();
        var r = Resource(1, "svc");
        var hour0 = BaseNano - BaseNano % Hour;
        const string trace = "0102030405060708090a0b0c0d0e0f10";
        await fx.Writer.FlushLogsAsync(
        [
            Log(r, hour0 + 5, 9, "a", trace, "0000000000000007"),
            Log(r, hour0 + Hour + 5, null, "b"),
            Log(r, hour0 + Hour + 6, null, "c"),
            new LogRecordModel { Resource = r, ObservedTimeUnixNano = hour0 + 2 * Hour, BodyValue = "no timestamp" }
        ]);

        Assert.Equal(3, fx.Store.EnumeratePartitions("log_records", 1, DateOnly.MinValue, DateOnly.MaxValue).Count());
        Assert.Equal(4, await RowsAsync(fx, "log_records"));
        Assert.Equal(1, await RowsAsync(fx, "log_records", "count(*)", "trace_hi = 72623859790382856 AND trace_lo = 651345242494996240 AND span_id = 7 AND severity_number = 9"));
        // a record without a timestamp is filed under its observed time
        Assert.Equal(1, await RowsAsync(fx, "log_records", "count(*)", $"time_unix_nano = {hour0 + 2 * Hour} AND body = 'no timestamp'"));
        Assert.Equal(1, await RowsAsync(fx, "log_records", "count(*)", "attributes['k'] = 'v' AND attributes['n'] = '5' AND body = 'a'"));

        Assert.Equal(4, await RowsAsync(fx, "log_rollup_minute", "sum(record_count)"));
        Assert.Equal(2, await RowsAsync(fx, "log_rollup_minute", "sum(record_count)", "severity_number = -1 AND bucket_start_unix_nano = " + (hour0 + Hour)));
    }

    [Fact]
    public async Task AllFiveMetricTypes_AreWritten_WithTheirNestedColumns_AndACatalogAndSeriesRow()
    {
        using var fx = new ParquetFixture();
        var r = Resource(1, "svc");
        var t = BaseNano;
        const string trace = "0102030405060708090a0b0c0d0e0f10";
        var exemplar = new ExemplarModel { TimeUnixNano = t, ValueDouble = 2.5, TraceIdHex = trace, SpanIdHex = "00000000000000aa", FilteredAttributes = new() { ["f"] = "x" } };
        var attrs = new Dictionary<string, object> { ["host"] = "h1" };

        var gauge = Metric(r, "g", MetricType.GAUGE);
        gauge.GaugeDataPoints = [new GaugeDataPointModel { TimeUnixNano = t, ValueInt = 3, Attributes = attrs, Exemplars = [exemplar] }];
        var sum = Metric(r, "s", MetricType.SUM);
        sum.SumDataPoints = [new SumDataPointModel { TimeUnixNano = t, StartTimeUnixNano = t - 1000, ValueDouble = 7, AggregationTemporality = AggregationTemporality.DELTA, IsMonotonic = true }];
        var hist = Metric(r, "h", MetricType.HISTOGRAM);
        hist.HistogramDataPoints = [new HistogramDataPointModel
        {
            TimeUnixNano = t, Count = 6, Sum = 12.5, Min = 1, Max = 5, BucketCounts = [1, 2, 3], ExplicitBounds = [1.5, 3],
            AggregationTemporality = AggregationTemporality.CUMULATIVE, Exemplars = [exemplar]
        }];
        var exp = Metric(r, "e", MetricType.EXPONENTIAL_HISTOGRAM);
        exp.ExponentialHistogramDataPoints = [new ExponentialHistogramDataPointModel
        {
            TimeUnixNano = t, Count = 4, Sum = 3, Scale = 2, ZeroCount = 1, PositiveOffset = 4, PositiveBucketCounts = [1, 2], NegativeOffset = 0, NegativeBucketCounts = [],
            AggregationTemporality = AggregationTemporality.DELTA
        }];
        var summary = Metric(r, "q", MetricType.SUMMARY);
        summary.SummaryDataPoints = [new SummaryDataPointModel
        {
            TimeUnixNano = t, Count = 9, Sum = 4.5, QuantileValues = [new QuantileValueModel { Quantile = 0.5, Value = 1.25 }, new QuantileValueModel { Quantile = 0.99, Value = 9 }]
        }];

        await fx.Writer.FlushMetricsAsync([gauge, sum, hist, exp, summary]);

        Assert.Equal(1, await RowsAsync(fx, "gauge_points", "count(*)", "value = 3 AND len(exemplars) = 1 AND exemplars[1].value = 2.5 AND exemplars[1].span_id = 170 AND exemplars[1].filtered_attributes['f'] = 'x' AND attributes['host'] = 'h1'"));
        Assert.Equal(1, await RowsAsync(fx, "sum_points", "count(*)", "value = 7 AND temporality = 'DELTA' AND is_monotonic AND start_time_unix_nano = " + (t - 1000)));
        Assert.Equal(1, await RowsAsync(fx, "histogram_points", "count(*)", "\"count\" = 6 AND bucket_counts = [1, 2, 3] AND explicit_bounds = [1.5, 3.0] AND \"max\" = 5 AND len(exemplars) = 1"));
        Assert.Equal(1, await RowsAsync(fx, "exp_histogram_points", "count(*)", "scale = 2 AND zero_count = 1 AND positive_offset = 4 AND positive_bucket_counts = [1, 2] AND len(negative_bucket_counts) = 0"));
        Assert.Equal(1, await RowsAsync(fx, "summary_points", "count(*)", "\"count\" = 9 AND quantiles[2].quantile = 0.99 AND quantiles[2].value = 9"));

        Assert.Equal(5, await RowsAsync(fx, "metric_catalog"));
        Assert.Equal(5, await RowsAsync(fx, "metric_series"));
        Assert.Equal(1, await RowsAsync(fx, "metric_series", "count(*)", "attributes['host'] = 'h1' AND metric_name = 'g' AND resource_attributes['service.name'] = 'svc'"));
        var gaugeId = (ulong)ParquetIds.MetricId(1, "svc", "g", MetricType.GAUGE);
        Assert.True(gaugeId != 0);
    }

    [Fact]
    public async Task ASeriesAndMetricKeepReporting_AreWrittenToTheCatalogOnlyOncePerRefreshPeriod()
    {
        using var fx = new ParquetFixture();
        var r = Resource(1, "svc");
        MetricModel M(long time)
        {
            var m = Metric(r, "g", MetricType.GAUGE);
            m.GaugeDataPoints = [new GaugeDataPointModel { TimeUnixNano = time, ValueDouble = 1 }];
            return m;
        }

        await fx.Writer.FlushMetricsAsync([M(BaseNano)]);
        await fx.Writer.FlushMetricsAsync([M(BaseNano + 1_000)]);

        Assert.Equal(2, await RowsAsync(fx, "gauge_points"));      // every point is stored ...
        Assert.Equal(1, await RowsAsync(fx, "metric_catalog"));    // ... the catalog and series once
        Assert.Equal(1, await RowsAsync(fx, "metric_series"));

        fx.Writer.ResetCatalogTrackers();                          // as after the files were deleted
        await fx.Writer.FlushMetricsAsync([M(BaseNano + 2_000)]);
        Assert.Equal(2, await RowsAsync(fx, "metric_catalog"));
    }

    [Fact]
    public async Task Compaction_KeepsLogsAndPointsAndFoldsCatalogAndSeriesRows()
    {
        using var fx = new ParquetFixture();
        var options = new ParquetOptions { RootPath = fx.Root, CatalogRefreshSeconds = 1 };
        options.Compaction.MinorFileThreshold = 1;
        options.Compaction.MinorAgeSeconds = 0;
        options.Compaction.CloseHourGraceMinutes = 0;
        options.Compaction.CloseDayGraceHours = 0;
        options.Compaction.GraceSeconds = 0;
        options.Compaction.DuckDb = new DuckDbOptions { MemoryLimit = "1GB", Threads = 2 };
        var writer = new ParquetBulkWriter(fx.Store, fx.FileWriter, new ManifestWriter(fx.Store, "w7"), NullLogger<ParquetBulkWriter>.Instance,
            parquetOptions: Options.Create(options));
        using var compactor = new ParquetCompactor(fx.Store, Options.Create(options), TimeProvider.System, NullLogger<ParquetCompactor>.Instance, new ParquetMetrics());
        var r = Resource(1, "svc");

        // four flushes of the same two series: the catalog and series are re-written each time (refresh 1 s, so wait between)
        for (var i = 0; i < 4; i++)
        {
            var m = Metric(r, "g", MetricType.GAUGE);
            m.GaugeDataPoints =
            [
                new GaugeDataPointModel { TimeUnixNano = BaseNano + i * 1_000L, ValueDouble = i, Attributes = new() { ["a"] = "1" } },
                new GaugeDataPointModel { TimeUnixNano = BaseNano + i * 1_000L, ValueDouble = i, Attributes = new() { ["a"] = "2" } }
            ];
            await writer.FlushMetricsAsync([m]);
            await writer.FlushLogsAsync([Log(r, BaseNano + i, i, "log " + i), Log(r, BaseNano + i + 10, i, "log")]);
            await Task.Delay(1100);
        }
        Assert.Equal(4, await RowsAsync(fx, "metric_catalog"));
        Assert.Equal(8, await RowsAsync(fx, "metric_series"));

        var result = await compactor.RunPassAsync(true, default);
        Assert.Equal(0, result.Failures);

        Assert.Equal(8, await RowsAsync(fx, "gauge_points"));
        Assert.Equal(8, await RowsAsync(fx, "log_records"));
        Assert.Equal(8, await RowsAsync(fx, "log_rollup_minute", "sum(record_count)"));
        Assert.Equal(1, await RowsAsync(fx, "metric_catalog"));
        Assert.Equal(2, await RowsAsync(fx, "metric_series"));
        Assert.Equal(BaseNano, await RowsAsync(fx, "metric_catalog", "min(first_seen_unix_nano)"));
        Assert.Equal(BaseNano + 3_000, await RowsAsync(fx, "metric_catalog", "max(last_seen_unix_nano)"));
        Assert.Equal(2, await RowsAsync(fx, "metric_series", "count(DISTINCT series_id)"));
        Assert.Equal(2, await RowsAsync(fx, "metric_series", "count(*)", "cardinality(attributes) = 1 AND resource_attributes['service.name'] = 'svc'"));
    }
}
