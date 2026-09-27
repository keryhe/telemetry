namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// The 40 fixed log-scale latency buckets (list-pages-server-side plan, Phase 3, decision 12):
/// growing x1.5 from 0.1 ms, so the same numbers are used on every provider and on both the trace
/// summary tables and the raw fallback path — no per-provider percentile SQL
/// (<c>percentile_cont</c>/<c>quantileExact</c>/none on MySQL). Percentiles are estimated from the
/// bucket counts with linear interpolation inside the winning bucket; worst-case error is half a
/// bucket. Mirrors <see cref="LogSeverityGroupSql"/>'s role for the six log-severity groups, kept
/// as its own type because the bucket shape (40 numeric ranges vs. 6 semantic ranges) and its
/// consumers (percentile estimation, not a severity label) are unrelated.
/// </summary>
public static class LatencyBucketSql
{
    public const int BucketCount = 40;

    /// <summary>Lower bound (inclusive), in milliseconds, of bucket i. Bucket 39's upper bound is unbounded.</summary>
    public static readonly double[] LowerBoundsMs = BuildLowerBoundsMs();

    /// <summary>Same bounds in nanoseconds (rounded), for comparing directly against a span's raw nanosecond duration without any floating-point division in SQL.</summary>
    public static readonly long[] LowerBoundsNano = [.. LowerBoundsMs.Select(ms => (long)Math.Round(ms * 1_000_000))];

    private static double[] BuildLowerBoundsMs()
    {
        var bounds = new double[BucketCount];
        for (var i = 0; i < BucketCount; i++)
            bounds[i] = 0.1 * Math.Pow(1.5, i);
        return bounds;
    }

    /// <summary>
    /// 40 <c>SUM(CASE WHEN ...)</c> columns aliased <c>Lb00</c>..<c>Lb39</c>, bucketing
    /// <paramref name="durationNanoExpr"/> (a whole-number nanosecond duration expression, e.g.
    /// <c>s.end_time_unix_nano - s.start_time_unix_nano</c>) against <see cref="LowerBoundsNano"/>.
    /// Integer comparisons only — no cast/division — so this is portable to every provider without
    /// a dialect hook.
    /// </summary>
    public static string SumCaseColumns(string durationNanoExpr)
    {
        var columns = new string[BucketCount];
        for (var i = 0; i < BucketCount; i++)
        {
            var lower = LowerBoundsNano[i];
            var upperClause = i < BucketCount - 1 ? $" AND {durationNanoExpr} < {LowerBoundsNano[i + 1]}" : "";
            columns[i] = $"SUM(CASE WHEN {durationNanoExpr} >= {lower}{upperClause} THEN 1 ELSE 0 END) AS Lb{i:D2}";
        }
        return string.Join(",\n       ", columns);
    }

    /// <summary>Column name list, e.g. for an INSERT's target column list: <c>lb_00, lb_01, ..., lb_39</c>.</summary>
    public static IEnumerable<string> ColumnNames() => Enumerable.Range(0, BucketCount).Select(i => $"lb_{i:D2}");

    /// <summary>
    /// Nearest-bucket percentile estimate (decision 12) from 40 bucket counts, indexed identically
    /// to <see cref="LowerBoundsMs"/>, with linear interpolation inside the winning bucket. Returns
    /// 0 when every bucket is empty ("no data", the same contract every other percentile in this
    /// codebase uses).
    /// </summary>
    public static double EstimatePercentile(IReadOnlyList<long> bucketCounts, double percentile)
    {
        var total = 0L;
        for (var i = 0; i < bucketCounts.Count; i++) total += bucketCounts[i];
        if (total <= 0) return 0;

        var rank = percentile / 100.0 * total;
        var cumulative = 0L;
        for (var i = 0; i < bucketCounts.Count; i++)
        {
            var count = bucketCounts[i];
            var nextCumulative = cumulative + count;
            if (rank <= nextCumulative || i == bucketCounts.Count - 1)
            {
                if (count <= 0) return LowerBoundsMs[i];
                var fractionIntoBucket = count == 0 ? 0 : (rank - cumulative) / count;
                fractionIntoBucket = Math.Clamp(fractionIntoBucket, 0, 1);
                var lower = LowerBoundsMs[i];
                // Last bucket has no upper bound: interpolate up to 2x its lower bound, an
                // arbitrary-but-bounded stand-in so a single outlier doesn't extrapolate to infinity.
                var upper = i < bucketCounts.Count - 1 ? LowerBoundsMs[i + 1] : lower * 2;
                return lower + fractionIntoBucket * (upper - lower);
            }
            cumulative = nextCumulative;
        }
        return LowerBoundsMs[^1];
    }
}
