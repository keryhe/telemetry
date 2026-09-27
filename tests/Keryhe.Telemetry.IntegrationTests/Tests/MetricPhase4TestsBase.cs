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
/// exponential-histogram scale downscaling, and exemplar paging (keyset on the analytics tier,
/// capped-500 on the standard tier, via <see cref="ProviderCapabilities.ExemplarPaging"/>).
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
        // First point (1000, no baseline) contributes delta 0; the reset (50, lower than 1000)
        // is treated as a fresh counter, contributing exactly 50 — never a huge negative delta
        // and never (1000 + 50) as if it were a plain diff.
        var total = series.Points.Sum(p => p.Value ?? 0);
        Assert.Equal(50, total);
        Assert.All(series.Points, p => Assert.True((p.Value ?? 0) >= 0, "cumulative delta must never go negative"));
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
    public async Task Exemplars_PageAllMatchingStreams_TierAppropriately()
    {
        var metric = SeededDataBuilder.GaugeWithExemplars(_fixture.TenantId, WindowStart, points: 30);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([metric]);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();
        var capabilities = readScope.ServiceProvider.GetRequiredService<ProviderCapabilities>();

        var page = await repo.GetMetricExemplarsAsync(new MetricExemplarQuery
        {
            MetricName = "phase4.exemplar.gauge",
            Start = WindowStart.AddSeconds(-1),
            End = WindowStart.AddSeconds(31),
            Size = 10
        });

        Assert.NotNull(page);
        if (capabilities.ExemplarPaging)
        {
            // Analytics tier: real keyset paging — a first page of size 10 must leave more to page
            // through (30 exemplars total), and walking `next` until exhausted must visit all 30
            // exactly once with no gaps or duplicates.
            Assert.NotNull(page!.NextCursor);
            var seen = new HashSet<long>(page.Exemplars.Select(e => e.Exemplar.TimeUnixNano));
            var cursor = page.NextCursor;
            while (cursor != null)
            {
                var next = await repo.GetMetricExemplarsAsync(new MetricExemplarQuery
                {
                    MetricName = "phase4.exemplar.gauge",
                    Start = WindowStart.AddSeconds(-1),
                    End = WindowStart.AddSeconds(31),
                    Size = 10,
                    Cursor = cursor,
                    Nav = "next"
                });
                Assert.NotNull(next);
                foreach (var e in next!.Exemplars)
                    Assert.True(seen.Add(e.Exemplar.TimeUnixNano), "keyset paging must not repeat an exemplar");
                cursor = next.NextCursor;
            }
            Assert.Equal(30, seen.Count);
        }
        else
        {
            // Standard tier: newest 500, no cursor.
            Assert.Null(page!.NextCursor);
            Assert.Equal(30, page.Exemplars.Count);
            Assert.False(page.Capped);
        }
    }
}
