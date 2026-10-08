using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Phase 4 correctness checks (list-pages-server-side plan): database-side bucketed aggregation
/// per metric type, top-N + "other" folding, mismatched-histogram-layout exclusion (decision 42),
/// exponential-histogram scale downscaling, and the capped, newest-first exemplar list.
/// </summary>
public abstract class MetricPhase4TestsBase : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    protected MetricPhase4TestsBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    [Fact]
    public async Task SumDelta_BucketsSum_NotLastValue()
    {
        var metric = SeededDataBuilder.SumDeltaSeries(_fixture.TenantId, WindowStart, points: 20, perPointValue: 3);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([metric]);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "phase4.orders.delta",
            Start = WindowStart.AddSeconds(-1),
            End = WindowStart.AddSeconds(21),
            Points = 1 // one big bucket covering everything
        });

        Assert.NotNull(result);
        var series = Assert.Single(result!.Series);
        var total = series.Points.Sum(p => p.Value ?? 0);
        Assert.Equal(20 * 3, total);
    }

    [Fact]
    public async Task SumCumulative_ResetDetection_DoesNotUnderflowOrExplode()
    {
        var (first, second) = SeededDataBuilder.CumulativeCounterReset(_fixture.TenantId, WindowStart);
        using (var writeScope = Scope())
        {
            var writer = writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
            await writer.FlushMetricsAsync([first]);
            await writer.FlushMetricsAsync([second]);
        }

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "phase0.requests.total",
            Start = WindowStart.AddSeconds(-1),
            End = WindowStart.AddSeconds(40),
            Points = 40
        });

        Assert.NotNull(result);
        var series = Assert.Single(result!.Series);
        // The first counter started inside the window, so all 1000 of its value accrued in-window;
        // the reset (50, lower than 1000, new start time) is a fresh counter contributing exactly
        // 50 — never a huge negative delta, and never 50 − 1000 as if it were a plain diff.
        var total = series.Points.Sum(p => p.Value ?? 0);
        Assert.Equal(1050, total);
        Assert.All(series.Points, p => Assert.True((p.Value ?? 0) >= 0, "cumulative delta must never go negative"));
    }

    [Fact]
    public async Task SumCumulative_CounterStartedBeforeWindow_FirstPointHasNoValue()
    {
        // Own service name: ResourceScopeCache outlives the per-test truncate of `metrics`, so
        // reusing the reset test's metric identity would hand this flush a stale metric id.
        var (first, second) = SeededDataBuilder.CumulativeCounterReset(_fixture.TenantId, WindowStart, serviceName: "requests-svc-late");
        using (var writeScope = Scope())
        {
            var writer = writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
            await writer.FlushMetricsAsync([first]);
            await writer.FlushMetricsAsync([second]);
        }

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        // The window opens after the first counter started (and there is no earlier point to use
        // as a baseline), so its in-window increase is unknowable: that bucket is left empty — not
        // charted as 0 — and only the post-reset counter's 50 is counted.
        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "phase0.requests.total",
            Start = WindowStart.AddSeconds(5),
            End = WindowStart.AddSeconds(40),
            Points = 35
        });

        Assert.NotNull(result);
        var series = Assert.Single(result!.Series);
        var valued = series.Points.Where(p => p.Value.HasValue).ToList();
        var only = Assert.Single(valued);
        Assert.Equal(50, only.Value);
    }

    [Fact]
    public async Task SumCumulative_RateUsesElapsedTimeNotBucketWidth()
    {
        // +15/15s → 1/s throughout; buckets are 0.1s wide, 150× narrower than the export interval.
        var metric = SeededDataBuilder.CumulativeCounterSeries(_fixture.TenantId, WindowStart, [15, 30, 45], intervalSeconds: 15);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([metric]);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "correctness.requests.cumulative",
            Start = WindowStart,
            End = WindowStart.AddSeconds(60),
            Points = 600
        });

        Assert.NotNull(result);
        var series = Assert.Single(result!.Series);
        var valued = series.Points.Where(p => p.Value.HasValue).ToList();
        Assert.Equal(3, valued.Count);
        Assert.Equal(45, valued.Sum(p => p.Value!.Value));
        Assert.All(valued, p => Assert.Equal(1.0, p.Rate!.Value, precision: 6));
    }

    /// <summary>
    /// Still-not-changed correctness plan, item 3: a cumulative histogram's reported min/max cover
    /// the stream's whole lifetime, not the bucket, so a per-bucket value can only be trusted when
    /// it's provably attributable to that bucket. Exercises all three cases via
    /// <see cref="SeededDataBuilder.CumulativeHistogramMinMaxSeries"/>: the lifetime max growing
    /// (exact), staying flat (a tightened bound estimate, flagged approximate), and a reset (exact).
    /// </summary>
    [Fact]
    public async Task HistogramCumulative_MinMaxApproximation_ExactWhenAttributable_ApproximateOtherwise()
    {
        var metric = SeededDataBuilder.CumulativeHistogramMinMaxSeries(_fixture.TenantId, WindowStart);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([metric]);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "correctness.minmax.cumulative",
            Start = WindowStart,
            End = WindowStart.AddSeconds(40),
            Points = 40
        });

        Assert.NotNull(result);
        var series = Assert.Single(result!.Series);
        var points = series.Points.Where(p => p.Count is > 0).OrderBy(p => p.Timestamp).ToList();
        Assert.Equal(3, points.Count);

        // +10s: lifetime min/max both moved (50 → 10, 250 → 350) — attributable to this bucket,
        // exact at the new values.
        Assert.Equal(10, points[0].Min);
        Assert.Equal(350, points[0].Max);
        Assert.False(points[0].MinMaxApproximate);

        // +20s: lifetime min/max unchanged — bound estimates, tightened to [100,200)'s own edges
        // (100, 200) since that's the only bucket this interval's new observations landed in.
        Assert.Equal(100, points[1].Min);
        Assert.Equal(200, points[1].Max);
        Assert.True(points[1].MinMaxApproximate);

        // +30s: reset (new start time) — min/max cover exactly this bucket's own lifetime, exact.
        Assert.Equal(99, points[2].Max);
        Assert.Equal(2, points[2].Min);
        Assert.False(points[2].MinMaxApproximate);
    }

    [Fact]
    public async Task HistogramCumulative_DifferencesSumAndCount()
    {
        // Cumulative (count, sum): 2 obs totalling 200ms, then 2 more totalling 100ms.
        var metric = SeededDataBuilder.CumulativeHistogramSeries(_fixture.TenantId, WindowStart, [(2, 200), (4, 300)], intervalSeconds: 10);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([metric]);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "correctness.latency.cumulative",
            Start = WindowStart,
            End = WindowStart.AddSeconds(30),
            Points = 30
        });

        Assert.NotNull(result);
        var series = Assert.Single(result!.Series);
        var valued = series.Points.Where(p => p.Count is > 0).ToList();
        Assert.Equal(2, valued.Count);
        // Summed across buckets, count and sum must equal the final cumulative totals — the
        // running sum re-added per bucket would give 500 (a 125ms mean instead of 75ms).
        Assert.Equal(4, valued.Sum(p => p.Count!.Value));
        Assert.Equal(300, valued.Sum(p => p.Sum!.Value), precision: 6);
        Assert.All(valued, p => Assert.Equal(0.2, p.Rate!.Value, precision: 6)); // 2 obs / 10s
    }

    /// <summary>
    /// A re-delivered metric batch stores every data point a second time: the five data-point tables are plain
    /// append targets since schema 3.0.0 (decision 7 — no unique key, no foreign keys), so reads must tolerate the
    /// repeat. Both windows here open AFTER the stream's first point, so the duplicated row is the one the
    /// cumulative loaders read as the stream's pre-window BASELINE. The totals are asserted, not just the absence
    /// of a throw: a baseline that went missing would make the first in-window increase unknowable and drop its
    /// bucket entirely, and a duplicate counted twice would inflate the deltas.
    ///
    /// <b>Scope.</b> This passes both before and after <c>ToDictionaryNewest</c> replaced the baseline lookup's
    /// plain <c>ToDictionary</c>, and that was measured on all five providers, not assumed: every provider's
    /// "last point per stream" SQL collapses appended duplicates before C# sees them (<c>ROW_NUMBER() ... WHERE
    /// rn = 1</c>, or <c>argMax</c> + <c>GROUP BY</c> on ClickHouse), which was also confirmed directly against
    /// MySQL including a deliberately spilled window sort. So re-delivery is NOT what produced the
    /// <c>metrics/series</c> 500s in the 3.0.1 stress ramp; this test guards the end-to-end append-duplication
    /// invariant, and <c>DuplicateBaselineRowTests</c> is the negative-controlled test of the guard itself.
    /// </summary>
    [Fact]
    public async Task RedeliveredCumulativeBatch_DuplicatesTheBaselineRow_StillReadsCorrectDeltas()
    {
        // Service names unique to this test, deliberately: the fixture's ResetAsync truncates `metrics` but keeps
        // the process-lifetime ResourceScopeCache (see PostgreSqlFixture.ResetAsync), so a test that reuses another
        // test's metric identity (resource + name + type + scope) hands whichever runs second a cached metric id
        // whose catalog row has been truncated away, and its series read comes back null.
        var counter = SeededDataBuilder.CumulativeCounterSeries(
            _fixture.TenantId, WindowStart, [15, 30, 45], intervalSeconds: 15, serviceName: "redelivered-rate-svc");
        var histogram = SeededDataBuilder.CumulativeHistogramSeries(
            _fixture.TenantId, WindowStart, [(2, 200), (4, 300)], intervalSeconds: 10, serviceName: "redelivered-latency-svc");
        using (var writeScope = Scope())
        {
            var writer = writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
            // Twice, as a retried OTLP export would arrive.
            await writer.FlushMetricsAsync([counter, histogram]);
            await writer.FlushMetricsAsync([counter, histogram]);
        }

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        // Window opens at +20s, so the (duplicated) +15s point is the baseline and +30s/+45s are in-window.
        var sum = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "correctness.requests.cumulative",
            Start = WindowStart.AddSeconds(20),
            End = WindowStart.AddSeconds(60),
            Points = 40
        });

        Assert.NotNull(sum);
        var sumSeries = Assert.Single(sum!.Series);
        var sumPoints = sumSeries.Points.Where(p => p.Value.HasValue).ToList();
        Assert.Equal(2, sumPoints.Count);                                    // +30s and +45s
        Assert.Equal(30, sumPoints.Sum(p => p.Value!.Value));                // (30-15) + (45-30)
        Assert.All(sumPoints, p => Assert.Equal(1.0, p.Rate!.Value, precision: 6));

        // Same shape for the histogram: baseline is the (duplicated) +10s point, +20s is in-window.
        var distribution = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "correctness.latency.cumulative",
            Start = WindowStart.AddSeconds(15),
            End = WindowStart.AddSeconds(30),
            Points = 15
        });

        Assert.NotNull(distribution);
        var histogramSeries = Assert.Single(distribution!.Series);
        var histogramPoints = histogramSeries.Points.Where(p => p.Count is > 0).ToList();
        Assert.Equal(2, histogramPoints.Sum(p => p.Count!.Value));           // 4 - 2
        Assert.Equal(100, histogramPoints.Sum(p => p.Sum!.Value), precision: 6); // 300 - 200
    }

    [Fact]
    public async Task HistogramDelta_MergesBucketCountsAndSumsCount()
    {
        var metric = SeededDataBuilder.HistogramDeltaSeries(_fixture.TenantId, WindowStart, points: 10);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([metric]);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "phase4.latency.delta",
            Start = WindowStart.AddSeconds(-1),
            End = WindowStart.AddSeconds(11),
            Points = 1
        });

        Assert.NotNull(result);
        var series = Assert.Single(result!.Series);
        var point = Assert.Single(series.Points);
        Assert.Equal(10 * 4, point.Count);
        Assert.NotNull(point.BucketCounts);
        Assert.Equal(new long[] { 10, 10, 10, 10, 0 }, point.BucketCounts);
        Assert.Equal(new[] { 10d, 50d, 100d, 500d }, point.BucketBounds);
    }

    [Fact]
    public async Task Histogram_MismatchedLayouts_ExcludesMinorityAndReportsCount()
    {
        var metrics = SeededDataBuilder.HistogramMismatchedLayouts(_fixture.TenantId, WindowStart);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync(metrics);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "phase4.mixed.histogram",
            Start = WindowStart.AddSeconds(-1),
            End = WindowStart.AddSeconds(5),
            Points = 1
        });

        Assert.NotNull(result);
        // Both streams share the same service name (decision 42 groups by explicit_bounds
        // *within* a display series), so they land in one display series with the majority layout
        // (100 observations) charted and the minority (5 observations) excluded.
        var series = Assert.Single(result!.Series);
        Assert.Equal(1, series.ExcludedStreams);
        var point = Assert.Single(series.Points);
        Assert.Equal(100, point.Count);
        Assert.Equal(new[] { 10d, 20d, 30d }, point.BucketBounds);
    }

    [Fact]
    public async Task ExpHistogram_DownscalesToCoarsestSharedScale()
    {
        var metric = SeededDataBuilder.ExpHistogramMultiScale(_fixture.TenantId, WindowStart);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([metric]);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "phase4.exp.histogram",
            Start = WindowStart.AddSeconds(-1),
            End = WindowStart.AddSeconds(5),
            Points = 1
        });

        Assert.NotNull(result);
        var series = Assert.Single(result!.Series);
        var point = Assert.Single(series.Points);
        // Total observation count across both points is preserved regardless of scale merging.
        Assert.Equal(12, point.Count);
        Assert.NotNull(point.BucketCounts);
        // Downscaling never loses observations: the merged bucket-count array's total must match.
        Assert.Equal(12L, point.BucketCounts!.Sum());
    }

    [Fact]
    public async Task Summary_MultiStream_AveragesQuantilesAndFlagsApproximate()
    {
        var a = SeededDataBuilder.SummarySeries(_fixture.TenantId, WindowStart, serviceName: "gateway-svc-a", points: 1);
        var b = SeededDataBuilder.SummarySeries(_fixture.TenantId, WindowStart, serviceName: "gateway-svc-a", points: 1);
        // Force both onto the SAME display series (same service name) but distinct streams by
        // using distinct resource instances under one service name.
        a.Resource = SeededDataBuilder.Resource(_fixture.TenantId, "gateway-svc", instanceId: "pod-a");
        b.Resource = SeededDataBuilder.Resource(_fixture.TenantId, "gateway-svc", instanceId: "pod-b");

        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([a, b]);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "phase4.gateway.summary",
            Start = WindowStart.AddSeconds(-1),
            End = WindowStart.AddSeconds(5),
            Points = 1
        });

        Assert.NotNull(result);
        var series = Assert.Single(result!.Series);
        var point = Assert.Single(series.Points);
        Assert.True(point.IsApproximate);
        Assert.NotNull(point.QuantileValues);
        // Both streams' p50 is 20 (i=0 in SummarySeries), so the average is exactly 20.
        Assert.Equal(20, point.QuantileValues![0]);
    }

    [Fact]
    public async Task TopN_KeepsEightAndFoldsRemainderIntoOther()
    {
        var metrics = SeededDataBuilder.ManyStreamsForTopN(_fixture.TenantId, WindowStart, streamCount: 10);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync(metrics);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "phase4.topn.gauge",
            Start = WindowStart.AddSeconds(-1),
            End = WindowStart.AddSeconds(5),
            Points = 1,
            Top = 8
        });

        Assert.NotNull(result);
        Assert.Equal(8, result!.Series.Count);
        Assert.NotNull(result.Other);
        Assert.Equal(2, result.Other!.SeriesCount);
        // The two smallest streams (values 200, 100) average to 150 in "other"'s gauge merge.
        var otherPoint = Assert.Single(result.Other.Points);
        Assert.Equal(150, otherPoint.Value);
        // The kept series must be strictly the eight largest (values 1000..300).
        Assert.All(result.Series, s => Assert.True(s.Points.Single().Value >= 300));
    }

    [Fact]
    public async Task Exemplars_AreTheNewestFirst_CappedAtTheLimit_AndSayWhenTruncated()
    {
        var metric = SeededDataBuilder.GaugeWithExemplars(_fixture.TenantId, WindowStart, points: 30);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([metric]);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        Task<MetricExemplarPage?> Exemplars(int limit) => repo.GetMetricExemplarsAsync(new MetricExemplarQuery
        {
            MetricName = "phase4.exemplar.gauge",
            Start = WindowStart.AddSeconds(-1),
            End = WindowStart.AddSeconds(31),
            Limit = limit
        });

        var all = await Exemplars(30);
        Assert.NotNull(all);
        Assert.Equal(30, all!.Exemplars.Count);
        Assert.False(all.Truncated); // exactly N exemplars exist: nothing more
        var times = all.Exemplars.Select(e => e.Exemplar.TimeUnixNano).ToList();
        Assert.Equal(times.Distinct().Count(), times.Count);
        Assert.Equal(times.OrderByDescending(t => t), times); // newest first

        var head = await Exemplars(10);
        Assert.Equal(times.Take(10), head!.Exemplars.Select(e => e.Exemplar.TimeUnixNano));
        Assert.True(head.Truncated);

        Assert.True((await Exemplars(29))!.Truncated);
        Assert.False((await Exemplars(100))!.Truncated);
    }
}
