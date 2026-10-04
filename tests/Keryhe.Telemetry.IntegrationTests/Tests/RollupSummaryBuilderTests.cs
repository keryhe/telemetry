using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>Pure tests of the window plan and the fold (plans/summary-rollups.md, decisions b and d). No Docker.</summary>
public class RollupSummaryBuilderTests
{
    private static readonly DateTime Far = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime D0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc); // a UTC day boundary

    [Theory]
    [InlineData(1, 60, 60)]          // 1h  -> 1 min x 60
    [InlineData(24, 1800, 48)]       // 24h -> 30 min x 48
    [InlineData(72, 7200, 36)]       // 3d  -> 2 h x 36
    [InlineData(168, 10800, 56)]     // 7d  -> 3 h x 56
    public void Ladder_PicksTheSmallestWidthWithAtMostBucketCountBuckets(int hours, int expectedSeconds, int expectedBuckets)
    {
        var window = RollupSummaryBuilder.PlanWindow(D0, D0.AddHours(hours), Far, 60);
        Assert.Equal(expectedSeconds, window.BucketSeconds);
        var result = RollupSummaryBuilder.BuildRequestSummary(window, [], Far);
        Assert.Equal(expectedBuckets, result.Buckets.Count);
        Assert.Equal(expectedSeconds, result.BucketSeconds);
        Assert.All(result.Buckets, b => Assert.Equal(expectedSeconds, b.CoveredSeconds));
    }

    [Fact]
    public void Ladder_Above60Days_UsesTheWidestWidth_AndReturnsMoreBuckets()
    {
        var window = RollupSummaryBuilder.PlanWindow(D0, D0.AddDays(90), Far, 60);
        Assert.Equal(86400, window.BucketSeconds);
        Assert.Equal(90, RollupSummaryBuilder.BuildRequestSummary(window, [], Far).Buckets.Count);
    }

    [Fact]
    public void Window_StartRoundsDown_EndRoundsUp_ToWholeMinutes()
    {
        var window = RollupSummaryBuilder.PlanWindow(D0.AddSeconds(90), D0.AddMinutes(10).AddSeconds(1), Far, 60);
        Assert.Equal(TimeConversion.DateTimeToUnixNano(D0.AddMinutes(1)), window.StartNano);
        Assert.Equal(TimeConversion.DateTimeToUnixNano(D0.AddMinutes(11)), window.EndNano);
        Assert.Equal(60, window.BucketSeconds);
    }

    [Fact]
    public void UnalignedWindow_HasPartialEdgeBuckets_WithTheirOwnCoveredSeconds()
    {
        // 02:30-05:30 at 3 h width... pick 7 d so the width is 3 h: 7d starting at 01:00 -> partial first and last buckets.
        var start = D0.AddHours(1);
        var window = RollupSummaryBuilder.PlanWindow(start, start.AddDays(7), Far, 60);
        Assert.Equal(10800, window.BucketSeconds);
        var result = RollupSummaryBuilder.BuildRequestSummary(window, [], Far);
        Assert.Equal(57, result.Buckets.Count);                       // bucketCount is a target, not a cap
        Assert.Equal(2 * 3600, result.Buckets[0].CoveredSeconds);      // 01:00 -> 03:00
        Assert.Equal(1 * 3600, result.Buckets[^1].CoveredSeconds);     // 00:00 -> 01:00
        Assert.Equal(7 * 86400, result.Buckets.Sum(b => b.CoveredSeconds));
    }

    [Fact]
    public void Window_IsClampedToWrittenThrough_AndBeyondItIsEmpty()
    {
        var wt = D0.AddHours(5);
        var window = RollupSummaryBuilder.PlanWindow(D0, D0.AddHours(8), wt, 60);
        Assert.Equal(TimeConversion.DateTimeToUnixNano(wt), window.EndNano);
        Assert.Equal(60, RollupSummaryBuilder.BuildRequestSummary(window, [], wt).Buckets.Count); // 5 h at 5 min

        var none = RollupSummaryBuilder.PlanWindow(D0.AddHours(6), D0.AddHours(7), wt, 60);
        Assert.True(none.IsEmpty);
        Assert.Empty(RollupSummaryBuilder.BuildRequestSummary(none, [], wt).Buckets);
    }

    [Fact]
    public void WrittenThrough_IsNowMinusTheMargins_RoundedDownToTheMinute()
    {
        var options = new RollupOptions { CloseGraceSeconds = 30, FlushIntervalSeconds = 15, ArrivalMarginSeconds = 60 };
        var wt = RollupSummaryBuilder.WrittenThrough(D0.AddMinutes(10).AddSeconds(20), options); // minus 105 s = 08:35 -> 08:00? 10:20-1:45 = 8:35
        Assert.Equal(D0.AddMinutes(8), wt);
        Assert.Equal(DateTimeKind.Utc, wt.Kind);
    }

    private static RequestRollupAggregate Row(DateTime bucket, string service, long count, long errors, long sumNanos, long maxNanos, params (int Band, long Count)[] bands)
    {
        var b = new long[DurationBands.Count];
        foreach (var (band, c) in bands) b[band] = c;
        return new RequestRollupAggregate
        {
            BucketStartNano = TimeConversion.DateTimeToUnixNano(bucket), Service = service,
            RequestCount = count, ErrorCount = errors, SumDurationNanos = sumNanos, MaxDurationNanos = maxNanos, Bands = b
        };
    }

    [Fact]
    public void Fold_ProducesBucketsServicesWindowTotalsAndTheLatencyGrid()
    {
        var window = RollupSummaryBuilder.PlanWindow(D0, D0.AddMinutes(3), Far, 60);
        var rows = new List<RequestRollupAggregate>
        {
            Row(D0, "web", 10, 1, 10 * 2_000_000L, 3_000_000, (4, 10)),
            Row(D0, "api", 5, 0, 5 * 1_000_000L, 1_500_000, (3, 5)),
            Row(D0.AddMinutes(2), "web", 5, 5, 5 * 40_000_000L, 90_000_000, (8, 5))
        };

        var r = RollupSummaryBuilder.BuildRequestSummary(window, rows, Far);

        Assert.Equal(3, r.Buckets.Count);
        Assert.Equal(15, r.Buckets[0].Count);
        Assert.Equal(1, r.Buckets[0].ErrorCount);
        Assert.Equal(0, r.Buckets[1].Count);
        Assert.Equal(0, r.Buckets[1].P99Ms);
        Assert.Equal(5, r.Buckets[2].ErrorCount);
        Assert.Equal(20, r.Summary.Count);
        Assert.Equal(6, r.Summary.ErrorCount);
        Assert.Equal((10 * 2.0 + 5 * 1.0 + 5 * 40.0) / 20, r.Summary.AvgMs, 6);
        Assert.Equal(90, r.Summary.MaxMs);
        Assert.Equal(20 / 180.0, r.Summary.RatePerSecond, 9);
        Assert.True(r.Summary.P50Ms <= r.Summary.P95Ms && r.Summary.P95Ms <= r.Summary.P99Ms);

        // Window percentiles come from the summed bands, not an average of bucket percentiles.
        var web = r.Services.Single(s => s.Service == "web");
        Assert.Equal(15, web.Count);
        Assert.Equal(6 / 15.0 * 100, web.ErrorRate, 6);
        Assert.Equal(15 / 180.0, web.RatePerSecond, 9);
        foreach (var s in r.Services)
            Assert.True(s.P50Ms <= s.P90Ms && s.P90Ms <= s.P95Ms, $"{s.Service}: p50 <= p90 <= p95");
        Assert.Equal("web", r.Services[0].Service); // most requests first

        Assert.Equal(3, r.Latency.Count);           // empty cells omitted
        var cell = r.Latency.Single(c => c.Band == 8);
        Assert.Equal(5, cell.Count);
        Assert.Equal(DurationBands.LowerEdgeNanos(8) / 1e6, cell.YStartMs, 9);
        Assert.Equal(DurationBands.UpperEdgeNanos(8) / 1e6, cell.YEndMs, 9);
        Assert.Equal(D0.AddMinutes(2), cell.XStart);
    }

    [Fact]
    public void Fold_TimedOut_IsEmptyAndFlagged()
    {
        var window = RollupSummaryBuilder.PlanWindow(D0, D0.AddMinutes(3), Far, 60);
        var r = RollupSummaryBuilder.BuildRequestSummary(window, [], Far, timedOut: true);
        Assert.True(r.TimedOut);
        Assert.Empty(r.Buckets);
        Assert.Equal(0, r.Summary.Count);
    }

    [Theory]
    [InlineData(-1, 2)]   // no severity counts as Info, even though Trace is <= 4
    [InlineData(0, 0)] [InlineData(4, 0)] [InlineData(5, 1)] [InlineData(8, 1)] [InlineData(9, 2)] [InlineData(12, 2)]
    [InlineData(13, 3)] [InlineData(16, 3)] [InlineData(17, 4)] [InlineData(20, 4)] [InlineData(21, 5)] [InlineData(24, 5)]
    public void SeverityGroup_MatchesLogSeverityGroupSql_WithTheNullSentinelAsInfo(int severity, int group)
        => Assert.Equal(group, RollupSummaryBuilder.SeverityGroup(severity));

    [Fact]
    public void LogFold_GroupsBySeverity_AndFillsEmptyBuckets()
    {
        var window = RollupSummaryBuilder.PlanWindow(D0, D0.AddMinutes(2), Far, 60);
        var start = TimeConversion.DateTimeToUnixNano(D0);
        var r = RollupSummaryBuilder.BuildLogSummary(window,
        [
            new LogRollupAggregate { BucketStartNano = start, SeverityNumber = -1, RecordCount = 2 },
            new LogRollupAggregate { BucketStartNano = start, SeverityNumber = 9, RecordCount = 3 },
            new LogRollupAggregate { BucketStartNano = start, SeverityNumber = 17, RecordCount = 4 },
            new LogRollupAggregate { BucketStartNano = start, SeverityNumber = 22, RecordCount = 1 }
        ], Far);

        Assert.Equal(10, r.Total);
        Assert.Equal(2, r.Buckets.Count);
        Assert.Equal(5, r.Buckets[0].Info);
        Assert.Equal(4, r.Buckets[0].Error);
        Assert.Equal(1, r.Buckets[0].Fatal);
        Assert.Equal(0, r.Buckets[1].Info);
    }
}
