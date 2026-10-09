using System.Text.Json;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// The provider-neutral half of the metric series pipeline (plans/clickhouse-redesign README R4), extracted unchanged from
/// <see cref="MetricReadRepositoryBase"/> so the relational providers and the ClickHouse repository share one definition of the
/// per-stream math: cumulative-to-delta conversion with reset detection, histogram and exponential-histogram deltas, the
/// min/max estimates, display-series merging, top-N ranking and the "other" fold. A provider's repository runs the SQL and
/// hands rows (<see cref="MetricPointRow"/>) or finished per-bucket aggregates (<see cref="StreamAgg"/>) to these functions.
/// </summary>
internal static class MetricSeriesPipeline
{
    internal static Dictionary<string, MetricPointRow> ToDictionaryNewest(
        IEnumerable<MetricPointRow> source, Func<MetricPointRow, string> key, Func<MetricPointRow, long> observedAt)
    {
        var result = new Dictionary<string, MetricPointRow>();
        foreach (var item in source)
        {
            var k = key(item);
            if (!result.TryGetValue(k, out var existing) || observedAt(item) > observedAt(existing))
                result[k] = item;
        }
        return result;
    }

    internal static Dictionary<string, object>? DeserializeAttributes(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(json);

    internal static string ConvertAttributeValueToString(object value) => DapperReadRepository.ConvertAttributeValueToString(value);

    /// <summary>
    /// Summaries are cumulative by definition: each bucket keeps its last point's quantile snapshot as-is, while count/sum become
    /// the increase since the previous observation under the same <see cref="ClassifyCumulativeStep"/> rule as the other
    /// cumulative types (null when unknowable).
    /// </summary>
    internal static Dictionary<string, StreamAgg> ComputeSummaryDeltas(
        IEnumerable<MetricPointRow> rows, Dictionary<string, MetricPointRow> baselineByStream, long windowStartNano)
    {
        var streams = new Dictionary<string, StreamAgg>();
        foreach (var group in rows.GroupBy(r => $"{r.MetricId}\u0001{r.AttributesJson}"))
        {
            long? prevCount = null, prevStart = null, prevTime = null;
            double? prevSum = null;
            if (baselineByStream.TryGetValue(group.Key, out var baseline))
            {
                prevCount = baseline.Count;
                prevSum = baseline.SumValue;
                prevStart = baseline.StartTimeUnixNano;
                prevTime = baseline.TimeUnixNano;
            }

            foreach (var r in group.OrderBy(r => r.Bucket))
            {
                var (step, seconds) = ClassifyCumulativeStep(r, prevTime, prevStart, r.Count < prevCount, windowStartNano);
                long? count = step switch
                {
                    CumulativeStep.Diff => r.Count - prevCount,
                    CumulativeStep.Whole => r.Count,
                    _ => null
                };
                double? sum = step switch
                {
                    CumulativeStep.Diff => SumIncrease(r.SumValue, prevSum),
                    CumulativeStep.Whole => r.SumValue,
                    _ => null
                };

                var quantiles = DeserializeArray<QuantileValueModel>(r.QuantileValues);
                GetOrAddStream(streams, r.MetricId, r.AttributesJson).Buckets[r.Bucket] = new BucketAgg
                {
                    Count = count,
                    Sum = sum,
                    Rate = count is { } c ? PerSecond(c, seconds) : null,
                    Quantiles = quantiles?.Select(q => q.Quantile).ToList(),
                    QuantileValues = quantiles?.Select(q => q.Value).ToList()
                };

                prevCount = r.Count;
                prevSum = r.SumValue;
                prevStart = r.StartTimeUnixNano;
                prevTime = r.TimeUnixNano;
            }
        }
        return streams;
    }

    /// <summary>Resolved bucketing parameters for one series request (decision 21).</summary>
    internal readonly record struct Bucketing(long StartNano, long EndNano, long BucketNanos, int Points);

    /// <summary>Minimum bucket width — guards against a division-by-near-zero window producing a degenerate bucket count.</summary>
    internal const long MinBucketNanos = 1_000_000; // 1 ms

    internal static Bucketing ComputeBucketing(DateTime start, DateTime end, int requestedPoints)
    {
        var points = Math.Clamp(requestedPoints, 1, 1000);
        var startNano = TimeConversion.DateTimeToUnixNano(start);
        var endNano = TimeConversion.DateTimeToUnixNano(end);
        var windowNano = Math.Max(1L, endNano - startNano);
        var bucketNanos = Math.Max(windowNano / points, MinBucketNanos);
        return new Bucketing(startNano, endNano, bucketNanos, points);
    }

    internal static StreamAgg GetOrAddStream(Dictionary<string, StreamAgg> streams, long metricId, string? attributesJson)
    {
        var json = attributesJson ?? "";
        var key = $"{metricId}\u0001{json}";
        if (!streams.TryGetValue(key, out var s))
        {
            s = new StreamAgg { MetricId = metricId, AttributesJson = json, Attributes = DeserializeAttributes(json) };
            streams[key] = s;
        }
        return s;
    }

    /// <summary>
    /// Per-second rate of a delta bucket: its total over the intervals its points cover. Falls back
    /// to the bucket width only for points with no start time, where no better interval exists.
    /// </summary>
    internal static double? DeltaRate(double total, double? coveredNanos, Bucketing b)
    {
        var nanos = coveredNanos is > 0 ? coveredNanos.Value : b.BucketNanos;
        return total / (nanos / 1e9);
    }

    /// <summary>How a cumulative point's value relates to the stream's previous observation.</summary>
    internal enum CumulativeStep
    {
        /// <summary>Same counter as the previous observation: the increase is the plain difference.</summary>
        Diff,
        /// <summary>
        /// A fresh counter — reset (value decreased or start time changed) or first observed inside
        /// the window after starting there — so its whole value accrued since its start time.
        /// </summary>
        Whole,
        /// <summary>No previous observation, and the counter started before the window: its increase within the window can't be known.</summary>
        Unknown
    }

    /// <summary>
    /// Classifies one cumulative point against the stream's previous observation (the pre-window
    /// baseline, or the last point of an earlier bucket) and returns the seconds its increase
    /// covers — used for every cumulative type so Sum, Histogram, ExpHistogram and Summary share
    /// one rule.
    /// </summary>
    internal static (CumulativeStep Step, double? Seconds) ClassifyCumulativeStep(
        MetricPointRow row, long? prevTime, long? prevStart, bool decreased, long windowStartNano)
    {
        var start = row.StartTimeUnixNano is long s && s > 0 && s < row.TimeUnixNano ? s : (long?)null;

        if (prevTime is null)
        {
            return start is { } st && st >= windowStartNano
                ? (CumulativeStep.Whole, (row.TimeUnixNano - st) / 1e9)
                : (CumulativeStep.Unknown, null);
        }

        var elapsed = row.TimeUnixNano > prevTime.Value ? (row.TimeUnixNano - prevTime.Value) / 1e9 : (double?)null;
        if (decreased || row.StartTimeUnixNano != prevStart)
            return (CumulativeStep.Whole, start is { } rs ? (row.TimeUnixNano - rs) / 1e9 : elapsed);

        return (CumulativeStep.Diff, elapsed);
    }

    internal static double? PerSecond(double increase, double? seconds) => seconds is > 0 ? increase / seconds.Value : null;

    /// <summary>
    /// Reset-detecting delta walk for cumulative sums: per stream, buckets are visited oldest-first
    /// (preceded by the pre-window baseline, if any) and each bucket's increase is classified by
    /// <see cref="ClassifyCumulativeStep"/> — the plain difference from the previous observation,
    /// the whole value for a counter that reset or started inside the window, and no bucket at all
    /// when neither applies (a long-running counter's first in-window point with no baseline: its
    /// in-window increase is unknowable, and reporting 0 would draw a false dip).
    /// </summary>
    internal static Dictionary<string, StreamAgg> ComputeCumulativeDeltas(
        List<MetricPointRow> bucketRows, List<MetricPointRow> baselineRows, long windowStartNano)
    {
        static double ValueOf(MetricPointRow r) => r.ValueDouble ?? r.ValueInt ?? 0;

        var streams = new Dictionary<string, StreamAgg>();
        var baselineByStream = ToDictionaryNewest(baselineRows, r => $"{r.MetricId}\u0001{r.AttributesJson}", r => r.TimeUnixNano);

        foreach (var group in bucketRows.GroupBy(r => $"{r.MetricId}\u0001{r.AttributesJson}"))
        {
            double? prevValue = null;
            long? prevStart = null, prevTime = null;
            if (baselineByStream.TryGetValue(group.Key, out var baseline))
            {
                prevValue = ValueOf(baseline);
                prevStart = baseline.StartTimeUnixNano;
                prevTime = baseline.TimeUnixNano;
            }

            foreach (var row in group.OrderBy(r => r.Bucket))
            {
                var curVal = ValueOf(row);
                var (step, seconds) = ClassifyCumulativeStep(row, prevTime, prevStart, curVal < prevValue, windowStartNano);
                if (step != CumulativeStep.Unknown)
                {
                    var delta = step == CumulativeStep.Diff ? curVal - prevValue!.Value : curVal;
                    GetOrAddStream(streams, row.MetricId, row.AttributesJson).Buckets[row.Bucket] =
                        new BucketAgg { Value = delta, Rate = PerSecond(delta, seconds) };
                }
                prevValue = curVal;
                prevStart = row.StartTimeUnixNano;
                prevTime = row.TimeUnixNano;
            }
        }
        return streams;
    }

    /// <summary>
    /// Histogram counterpart of <see cref="ComputeCumulativeDeltas"/>, under the same
    /// <see cref="ClassifyCumulativeStep"/> rule: bucket counts, count and sum are all turned into
    /// the increase since the previous observation (sum included — it is cumulative too, so
    /// passing it through raw would re-add the running total once per bucket). Min/Max stay as
    /// reported: a cumulative point's min/max cover the whole stream lifetime and can't be
    /// differenced.
    /// </summary>
    internal static Dictionary<string, StreamAgg> ComputeHistogramCumulativeDeltas(
        List<MetricPointRow> bucketRows, List<MetricPointRow> baselineRows, long windowStartNano)
    {
        var streams = new Dictionary<string, StreamAgg>();
        var baselineByStream = ToDictionaryNewest(baselineRows, r => $"{r.MetricId}\u0001{r.AttributesJson}", r => r.TimeUnixNano);

        foreach (var group in bucketRows.GroupBy(r => $"{r.MetricId}\u0001{r.AttributesJson}"))
        {
            var ordered = group.OrderBy(r => r.Bucket).ToList();
            var bounds = DeserializeDoubleArray(ordered.Select(r => r.ExplicitBounds).FirstOrDefault(x => x != null));

            long[]? prevCounts = null;
            double? prevSum = null, prevMin = null, prevMax = null;
            long? prevStart = null, prevTime = null;
            if (baselineByStream.TryGetValue(group.Key, out var baseline))
            {
                prevCounts = DeserializeLongArray(baseline.BucketCounts);
                prevSum = baseline.SumValue;
                prevMin = baseline.MinValue;
                prevMax = baseline.MaxValue;
                prevStart = baseline.StartTimeUnixNano;
                prevTime = baseline.TimeUnixNano;
            }

            foreach (var row in ordered)
            {
                var curCounts = DeserializeLongArray(row.BucketCounts) ?? Array.Empty<long>();
                var decreased = prevCounts != null && prevCounts.Length == curCounts.Length && curCounts.Sum() < prevCounts.Sum();
                var (step, seconds) = ClassifyCumulativeStep(row, prevTime, prevStart, decreased, windowStartNano);

                if (step != CumulativeStep.Unknown)
                {
                    // A changed bucket layout can't be differenced element-wise; count it whole.
                    var whole = step == CumulativeStep.Whole || prevCounts == null || prevCounts.Length != curCounts.Length;
                    long[] delta;
                    if (whole)
                    {
                        delta = curCounts;
                    }
                    else
                    {
                        delta = new long[curCounts.Length];
                        for (var i = 0; i < curCounts.Length; i++)
                            delta[i] = Math.Max(0, curCounts[i] - prevCounts![i]);
                    }

                    // See MetricBucketPoint.MinMaxApproximate: exact when `whole` (the reported
                    // min/max cover exactly this bucket's own lifetime) or when the lifetime extreme
                    // moved (it necessarily moved within this interval); otherwise a bound estimate,
                    // tightened to the edge of the highest/lowest bucket that got new observations.
                    var (estMax, maxApprox) = EstimateMax(row.MaxValue, prevMax, whole, HighestNonEmptyUpperBound(delta, bounds));
                    var (estMin, minApprox) = EstimateMin(row.MinValue, prevMin, whole, LowestNonEmptyLowerBound(delta, bounds));

                    var stream = GetOrAddStream(streams, row.MetricId, row.AttributesJson);
                    stream.Bounds ??= bounds;
                    var count = delta.Sum();
                    stream.Buckets[row.Bucket] = new BucketAgg
                    {
                        Count = count,
                        Sum = whole ? row.SumValue : SumIncrease(row.SumValue, prevSum),
                        Min = estMin,
                        Max = estMax,
                        MinMaxApproximate = minApprox || maxApprox,
                        Counts = delta,
                        Rate = PerSecond(count, seconds)
                    };
                }

                prevCounts = curCounts;
                prevSum = row.SumValue;
                prevMin = row.MinValue;
                prevMax = row.MaxValue;
                prevStart = row.StartTimeUnixNano;
                prevTime = row.TimeUnixNano;
            }
        }
        return streams;
    }

    /// <summary>A cumulative sum's increase over the previous observation, clamped at 0 (float noise / non-monotonic sums).</summary>
    internal static double? SumIncrease(double? current, double? previous) =>
        current is { } c && previous is { } p ? Math.Max(0, c - p) : current;

    /// <summary>
    /// Upper edge (explicit bound) of the highest-indexed bucket that received a new (delta &gt; 0)
    /// observation this interval, or null when that bucket is the unbounded overflow bucket (no
    /// finite edge) or nothing in <paramref name="delta"/> is non-empty. Used to tighten a cumulative
    /// histogram bucket's approximate Max estimate (see <see cref="MetricBucketPoint.MinMaxApproximate"/>).
    /// </summary>
    internal static double? HighestNonEmptyUpperBound(long[] delta, double[]? bounds)
    {
        for (var i = delta.Length - 1; i >= 0; i--)
        {
            if (delta[i] <= 0) continue;
            return bounds != null && i < bounds.Length ? bounds[i] : null;
        }
        return null;
    }

    /// <summary>Lower-edge counterpart of <see cref="HighestNonEmptyUpperBound"/>, for the approximate Min estimate. The first bucket's lower edge is 0 (explicit-bounds histograms assume non-negative values).</summary>
    internal static double? LowestNonEmptyLowerBound(long[] delta, double[]? bounds)
    {
        for (var i = 0; i < delta.Length; i++)
        {
            if (delta[i] <= 0) continue;
            return i == 0 ? 0.0 : bounds != null && i - 1 < bounds.Length ? bounds[i - 1] : null;
        }
        return null;
    }

    /// <summary>
    /// Estimates one bucket's Max from a cumulative point's lifetime <paramref name="curMax"/>:
    /// exact when the lifetime max grew since <paramref name="prevMax"/> (the growth necessarily
    /// happened in this bucket's interval) or when <paramref name="exactWhole"/> (the counter
    /// started/reset here, so the whole lifetime value belongs to this bucket) — otherwise an
    /// upper-bound estimate, tightened against <paramref name="upperEdge"/> when known.
    /// </summary>
    internal static (double? Value, bool Approximate) EstimateMax(double? curMax, double? prevMax, bool exactWhole, double? upperEdge)
    {
        if (curMax is not { } cm) return (null, false);
        if (exactWhole || prevMax is null || cm > prevMax.Value) return (cm, false);
        return upperEdge is { } u ? (Math.Min(u, cm), true) : (cm, true);
    }

    /// <summary>Min counterpart of <see cref="EstimateMax"/>.</summary>
    internal static (double? Value, bool Approximate) EstimateMin(double? curMin, double? prevMin, bool exactWhole, double? lowerEdge)
    {
        if (curMin is not { } cm) return (null, false);
        if (exactWhole || prevMin is null || cm < prevMin.Value) return (cm, false);
        return lowerEdge is { } l ? (Math.Max(l, cm), true) : (cm, true);
    }

    /// <summary>Exponential-histogram counterpart of <see cref="ComputeHistogramCumulativeDeltas"/>.</summary>
    internal static Dictionary<string, StreamAgg> ComputeExpHistogramCumulativeDeltas(
        List<MetricPointRow> bucketRows, List<MetricPointRow> baselineRows, long windowStartNano)
    {
        var streams = new Dictionary<string, StreamAgg>();
        var baselineByStream = ToDictionaryNewest(baselineRows, r => $"{r.MetricId}\u0001{r.AttributesJson}", r => r.TimeUnixNano);
        var globalTargetScale = ComputeGlobalMinScale(bucketRows, baselineRows);

        foreach (var group in bucketRows.GroupBy(r => $"{r.MetricId}\u0001{r.AttributesJson}"))
        {
            Dictionary<long, long>? prevMap = null;
            long? prevZero = null;
            double? prevSum = null, prevMin = null, prevMax = null;
            long? prevStart = null, prevTime = null;
            if (baselineByStream.TryGetValue(group.Key, out var baseline))
            {
                prevMap = DownscaleRow(baseline, globalTargetScale);
                prevZero = baseline.ZeroCount;
                prevSum = baseline.SumValue;
                prevMin = baseline.MinValue;
                prevMax = baseline.MaxValue;
                prevStart = baseline.StartTimeUnixNano;
                prevTime = baseline.TimeUnixNano;
            }

            foreach (var row in group.OrderBy(r => r.Bucket))
            {
                var curMap = DownscaleRow(row, globalTargetScale);
                var curZero = row.ZeroCount ?? 0;
                var curTotal = curMap.Values.Sum() + curZero;
                var prevTotal = (prevMap?.Values.Sum() ?? 0) + (prevZero ?? 0);
                var (step, seconds) = ClassifyCumulativeStep(row, prevTime, prevStart, prevMap != null && curTotal < prevTotal, windowStartNano);

                if (step != CumulativeStep.Unknown)
                {
                    var whole = step == CumulativeStep.Whole || prevMap == null;
                    var delta = new Dictionary<long, long>();
                    foreach (var kv in curMap)
                        delta[kv.Key] = whole ? kv.Value : Math.Max(0, kv.Value - prevMap!.GetValueOrDefault(kv.Key));
                    var deltaZero = whole || prevZero == null ? curZero : Math.Max(0, curZero - prevZero.Value);
                    var count = delta.Values.Sum() + deltaZero;

                    // Same exactness rule as the explicit-bounds histogram (see
                    // MetricBucketPoint.MinMaxApproximate), but with no tightened edge: recovering a
                    // real bucket boundary from an (index, scale) pair isn't implemented anywhere in
                    // this read path yet (see FinalizeExpHistogramBounds's doc comment on that same
                    // gap), so an inexact point here is flagged approximate at the lifetime value
                    // rather than given a falsely-precise tightened number.
                    var (estMax, maxApprox) = EstimateMax(row.MaxValue, prevMax, whole, null);
                    var (estMin, minApprox) = EstimateMin(row.MinValue, prevMin, whole, null);

                    GetOrAddStream(streams, row.MetricId, row.AttributesJson).Buckets[row.Bucket] = new BucketAgg
                    {
                        Count = count,
                        Sum = whole ? row.SumValue : SumIncrease(row.SumValue, prevSum),
                        Min = estMin,
                        Max = estMax,
                        MinMaxApproximate = minApprox || maxApprox,
                        SparseCounts = delta,
                        ZeroCountValue = deltaZero,
                        Rate = PerSecond(count, seconds)
                    };
                }

                prevMap = curMap;
                prevZero = curZero;
                prevSum = row.SumValue;
                prevMin = row.MinValue;
                prevMax = row.MaxValue;
                prevStart = row.StartTimeUnixNano;
                prevTime = row.TimeUnixNano;
            }
        }
        return streams;
    }

    internal static int ComputeGlobalMinScale(params IEnumerable<MetricPointRow>[] rowSets)
    {
        var min = int.MaxValue;
        foreach (var set in rowSets)
            foreach (var r in set)
                if (r.Scale is { } s && s < min) min = s;
        return min == int.MaxValue ? 0 : min;
    }

    /// <summary>
    /// Downscales one exponential-histogram row's positive buckets to <paramref name="targetScale"/>
    /// (the coarsest/smallest scale observed anywhere in this query's result set), keyed by
    /// absolute exponent index — ported from the client's <c>normalizeExpHistogramSeries</c>
    /// (<c>chart.utils.ts</c>), which this phase retires from the client in favor of this
    /// server-side equivalent. Negative buckets are not represented in the read path today,
    /// matching the ported original.
    /// </summary>
    internal static Dictionary<long, long> DownscaleRow(MetricPointRow row, int targetScale)
    {
        var map = new Dictionary<long, long>();
        var counts = DeserializeLongArray(row.PositiveBucketCounts);
        if (counts == null || row.Scale is not { } scale) return map;

        var factor = Math.Pow(2, scale - targetScale); // >= 1
        var offset = row.PositiveOffset ?? 0;
        for (var k = 0; k < counts.Length; k++)
        {
            if (counts[k] == 0) continue;
            var jPrime = (long)Math.Floor((offset + k) / factor);
            map[jPrime] = map.GetValueOrDefault(jPrime) + counts[k];
        }
        return map;
    }

    /// <summary>
    /// Histogram/exponential-histogram/summary streams merge into one display series per service,
    /// ignoring labels — mirroring the pre-Phase-4 client, which always computed one windowed
    /// aggregate across every series for these three types (<c>aggregateHistogramWindows</c>/
    /// <c>aggregateSummaryWindows</c> over the whole <c>multi.series</c> list, never per label set).
    /// Gauge/Sum keep the label-inclusive <see cref="SeriesKey"/> (one line per pod/label-set), the
    /// client's existing per-series behavior for those types. Without this, decision 42's
    /// mismatched-bucket-layout grouping could only ever trigger for two streams sharing the exact
    /// same label set — an edge case (a service that changed its histogram config without changing
    /// labels) far narrower than its actual purpose: multiple pods of one service, each fine on its
    /// own, disagreeing on bucket layout. Found via the Phase 4 integration tests (the mismatched-
    /// layout test seeds two pods of the same service with different <c>k8s.pod.name</c> values).
    /// </summary>
    internal static bool IsDistributionType(MetricType type) =>
        type is MetricType.HISTOGRAM or MetricType.EXPONENTIAL_HISTOGRAM or MetricType.SUMMARY;

    internal static (List<DisplayMetricSeries> Series, OtherMetricSeries? Other) BuildDisplaySeriesAndOther(
        MetricType type, List<StreamAgg> streams, Bucketing b, int top)
    {
        var isDistribution = IsDistributionType(type);
        var byDisplayKey = streams.GroupBy(s => isDistribution
                ? s.ServiceName
                : SeriesKey(s.ServiceName, ToLabelDictionary(s.Attributes)))
            .Select(g => g.ToList())
            .ToList();

        var built = new List<(DisplayMetricSeries Series, double Rank)>();
        foreach (var group in byDisplayKey)
        {
            // Distributions fold every contributing pod's labels away (see IsDistributionType's
            // doc comment); Gauge/Sum keep the single stream's own label set.
            var labels = isDistribution ? new Dictionary<string, string>() : ToLabelDictionary(group[0].Attributes);
            var serviceName = group[0].ServiceName;
            var (points, excluded) = MergeBucketsForDisplaySeries(type, group, b);
            var series = new DisplayMetricSeries
            {
                SeriesName = BuildSeriesDisplayName(serviceName, labels),
                ServiceName = serviceName,
                Labels = labels,
                Points = points,
                ExcludedStreams = excluded > 0 ? excluded : null
            };
            built.Add((series, RankingMeasure(type, points)));
        }

        var ranked = built.OrderByDescending(x => x.Rank).ToList();
        var kept = ranked.Take(top).Select(x => x.Series).ToList();
        var rest = ranked.Skip(top).Select(x => x.Series).ToList();

        OtherMetricSeries? other = null;
        if (rest.Count > 0)
        {
            var (points, excluded) = MergeDisplaySeriesIntoOther(type, rest, b);
            other = new OtherMetricSeries { SeriesCount = rest.Count, Points = points, ExcludedStreams = excluded > 0 ? excluded : null };
        }

        return (kept, other);
    }

    /// <summary>The measure display series are ranked by (decision 23): total observed magnitude across the window — mirrors the client's former <c>groupValue</c> (window increase for counters, Σ for deltas/histograms/summaries).</summary>
    internal static double RankingMeasure(MetricType type, List<MetricBucketPoint> points) => type switch
    {
        MetricType.GAUGE => points.Where(p => p.Value.HasValue).Select(p => p.Value!.Value).DefaultIfEmpty(0).Average(),
        MetricType.SUM => points.Sum(p => p.Value ?? 0),
        MetricType.HISTOGRAM or MetricType.EXPONENTIAL_HISTOGRAM or MetricType.SUMMARY => points.Sum(p => p.Count ?? 0),
        _ => 0
    };

    internal static (List<MetricBucketPoint> Points, int Excluded) MergeBucketsForDisplaySeries(MetricType type, List<StreamAgg> streams, Bucketing b)
    {
        var excluded = 0;
        if (type == MetricType.HISTOGRAM)
        {
            var byBounds = streams.GroupBy(s => BoundsSignature(s.Bounds)).ToList();
            if (byBounds.Count > 1)
            {
                var winner = byBounds.OrderByDescending(g => g.Sum(s => s.Buckets.Values.Sum(bk => bk.Count ?? 0))).First();
                excluded = streams.Count - winner.Count();
                streams = winner.ToList();
            }
        }

        var points = new List<MetricBucketPoint>(b.Points);
        for (var i = 0; i < b.Points; i++)
            points.Add(MergeBucketAcrossStreams(type, streams, i, TimeConversion.UnixNanoToDateTime(b.StartNano + i * b.BucketNanos)));

        if (type == MetricType.EXPONENTIAL_HISTOGRAM)
            FinalizeExpHistogramBounds(points, streams);

        return (points, excluded);
    }

    internal static string BoundsSignature(double[]? bounds)
        => bounds == null ? "" : string.Join(",", bounds.Select(v => v.ToString("R")));

    internal static MetricBucketPoint MergeBucketAcrossStreams(MetricType type, List<StreamAgg> streams, int bucketIdx, DateTime ts)
    {
        var contributing = streams.Select(s => s.Buckets.GetValueOrDefault(bucketIdx)).Where(x => x != null).Select(x => x!).ToList();
        var point = new MetricBucketPoint { Timestamp = ts };
        if (contributing.Count == 0) return point;

        switch (type)
        {
            case MetricType.GAUGE:
                var avgs = contributing.Where(c => c.Avg.HasValue).Select(c => c.Avg!.Value).ToList();
                if (avgs.Count > 0) point.Value = avgs.Average();
                var mins = contributing.Where(c => c.Min.HasValue).Select(c => c.Min!.Value).ToList();
                if (mins.Count > 0) point.Min = mins.Min();
                var maxs = contributing.Where(c => c.Max.HasValue).Select(c => c.Max!.Value).ToList();
                if (maxs.Count > 0) point.Max = maxs.Max();
                break;

            case MetricType.SUM:
                point.Value = contributing.Sum(c => c.Value ?? 0);
                point.Rate = SumOrNull(contributing.Select(c => c.Rate));
                break;

            case MetricType.HISTOGRAM:
                point.Count = contributing.Sum(c => c.Count ?? 0);
                point.Sum = contributing.Sum(c => c.Sum ?? 0);
                point.Rate = SumOrNull(contributing.Select(c => c.Rate));
                var hMins = contributing.Where(c => c.Min.HasValue).Select(c => c.Min!.Value).ToList();
                if (hMins.Count > 0) point.Min = hMins.Min();
                var hMaxs = contributing.Where(c => c.Max.HasValue).Select(c => c.Max!.Value).ToList();
                if (hMaxs.Count > 0) point.Max = hMaxs.Max();
                point.MinMaxApproximate = contributing.Any(c => c.MinMaxApproximate);
                var len = contributing.FirstOrDefault(c => c.Counts != null)?.Counts?.Length;
                if (len is { } l)
                {
                    var merged = new long[l];
                    foreach (var c in contributing)
                        if (c.Counts != null && c.Counts.Length == l)
                            for (var i = 0; i < l; i++) merged[i] += c.Counts[i];
                    point.BucketCounts = merged.ToList();
                    point.BucketBounds = streams[0].Bounds?.ToList();
                }
                break;

            case MetricType.EXPONENTIAL_HISTOGRAM:
                point.Count = contributing.Sum(c => c.Count ?? 0);
                point.Sum = contributing.Sum(c => c.Sum ?? 0);
                point.Rate = SumOrNull(contributing.Select(c => c.Rate));
                var eMins = contributing.Where(c => c.Min.HasValue).Select(c => c.Min!.Value).ToList();
                if (eMins.Count > 0) point.Min = eMins.Min();
                var eMaxs = contributing.Where(c => c.Max.HasValue).Select(c => c.Max!.Value).ToList();
                if (eMaxs.Count > 0) point.Max = eMaxs.Max();
                point.MinMaxApproximate = contributing.Any(c => c.MinMaxApproximate);
                // BucketCounts/BucketBounds are filled in by FinalizeExpHistogramBounds once every
                // bucket's sparse map is known (needs the series-wide index range).
                break;

            case MetricType.SUMMARY:
                // Count/Sum are null for a stream whose increase is unknowable (see LoadSummaryAsync) —
                // left null rather than 0 when no stream knows it, so no false dip is charted.
                if (contributing.Any(c => c.Count.HasValue)) point.Count = contributing.Sum(c => c.Count ?? 0);
                if (contributing.Any(c => c.Sum.HasValue)) point.Sum = contributing.Sum(c => c.Sum ?? 0);
                point.Rate = SumOrNull(contributing.Select(c => c.Rate));
                point.IsApproximate = contributing.Count > 1;
                var withQuantiles = contributing.Where(c => c.Quantiles is { Count: > 0 }).ToList();
                if (withQuantiles.Count > 0)
                {
                    var qLen = withQuantiles[0].Quantiles!.Count;
                    if (withQuantiles.All(c => c.QuantileValues!.Count == qLen))
                    {
                        point.Quantiles = withQuantiles[0].Quantiles;
                        point.QuantileValues = Enumerable.Range(0, qLen)
                            .Select(i => withQuantiles.Average(c => c.QuantileValues![i]))
                            .ToList();
                    }
                }
                break;
        }

        return point;
    }

    /// <summary>
    /// After every bucket's sparse exponential-histogram map is merged (decision 22/42's exp-histogram
    /// note), computes the display series' own index range once and materializes each bucket's
    /// dense <c>BucketCounts</c>/<c>BucketBounds</c> against it (zero-filling buckets with no
    /// observation at a given index) — the per-display-series equivalent of the ported
    /// <c>normalizeExpHistogramSeries</c>.
    /// </summary>
    internal static void FinalizeExpHistogramBounds(List<MetricBucketPoint> points, List<StreamAgg> streams)
    {
        var sparseByBucket = new Dictionary<int, (Dictionary<long, long> Map, long Zero)>();
        for (var i = 0; i < points.Count; i++)
        {
            var contributing = streams.Select(s => s.Buckets.GetValueOrDefault(i)).Where(x => x?.SparseCounts != null).Select(x => x!).ToList();
            if (contributing.Count == 0) continue;
            var map = new Dictionary<long, long>();
            long zero = 0;
            foreach (var c in contributing)
            {
                foreach (var kv in c.SparseCounts!) map[kv.Key] = map.GetValueOrDefault(kv.Key) + kv.Value;
                zero += c.ZeroCountValue ?? 0;
            }
            sparseByBucket[i] = (map, zero);
        }
        if (sparseByBucket.Count == 0) return;

        var minIdx = sparseByBucket.Values.SelectMany(v => v.Map.Keys).DefaultIfEmpty(0).Min();
        var maxIdx = sparseByBucket.Values.SelectMany(v => v.Map.Keys).DefaultIfEmpty(0).Max();
        var n = sparseByBucket.Values.Any(v => v.Map.Count > 0) ? (int)(maxIdx - minIdx + 1) : 0;

        // Target scale isn't tracked per display series (it was resolved once, globally, at load
        // time — see LoadDistributionDeltaAsync/ComputeExpHistogramCumulativeDeltas) so the bound
        // values here use log2Base = 1 (i.e. bucket index only); callers that need the true bound
        // *value* recompute it from BucketCounts.Length/positive index — recorded as a known gap,
        // see the deviations note in the plan.
        var bounds = new List<double>();
        for (var m = 0; m < n; m++) bounds.Add(minIdx + m);

        foreach (var (bucketIdx, (map, zero)) in sparseByBucket)
        {
            var counts = new long[n + 1];
            counts[0] = zero;
            foreach (var kv in map) counts[kv.Key - minIdx + 1] = kv.Value;
            points[bucketIdx].BucketCounts = counts.ToList();
            points[bucketIdx].BucketBounds = bounds;
        }
    }

    internal static (List<MetricBucketPoint> Points, int Excluded) MergeDisplaySeriesIntoOther(MetricType type, List<DisplayMetricSeries> excludedSeries, Bucketing b)
    {
        var totalExcludedStreams = excludedSeries.Sum(s => s.ExcludedStreams ?? 0);
        var points = new List<MetricBucketPoint>(b.Points);

        if (type == MetricType.HISTOGRAM)
        {
            var byLen = excludedSeries.GroupBy(s => s.Points.FirstOrDefault(p => p.BucketCounts != null)?.BucketCounts?.Count ?? -1).ToList();
            if (byLen.Count > 1)
            {
                var winner = byLen.OrderByDescending(g => g.Sum(s => s.Points.Sum(p => p.Count ?? 0))).First();
                totalExcludedStreams += excludedSeries.Count - winner.Count();
                excludedSeries = winner.ToList();
            }
        }

        for (var i = 0; i < b.Points; i++)
        {
            var ts = TimeConversion.UnixNanoToDateTime(b.StartNano + i * b.BucketNanos);
            var contributing = excludedSeries.Select(s => i < s.Points.Count ? s.Points[i] : null).Where(p => p != null).Select(p => p!).ToList();
            points.Add(MergePointsOfType(type, contributing, ts));
        }

        return (points, totalExcludedStreams);
    }

    /// <summary>Σ of the known values, or null when none is known (a rate nobody could measure isn't 0/s).</summary>
    internal static double? SumOrNull(IEnumerable<double?> values)
    {
        double? total = null;
        foreach (var v in values)
            if (v.HasValue) total = (total ?? 0) + v.Value;
        return total;
    }

    internal static MetricBucketPoint MergePointsOfType(MetricType type, List<MetricBucketPoint> contributing, DateTime ts)
    {
        var point = new MetricBucketPoint { Timestamp = ts };
        var withValue = contributing.Where(p => p.Value.HasValue || p.Count.HasValue || p.Quantiles != null).ToList();
        if (withValue.Count == 0) return point;

        switch (type)
        {
            case MetricType.GAUGE:
                var avgs = contributing.Where(p => p.Value.HasValue).Select(p => p.Value!.Value).ToList();
                if (avgs.Count > 0) point.Value = avgs.Average();
                var mins = contributing.Where(p => p.Min.HasValue).Select(p => p.Min!.Value).ToList();
                if (mins.Count > 0) point.Min = mins.Min();
                var maxs = contributing.Where(p => p.Max.HasValue).Select(p => p.Max!.Value).ToList();
                if (maxs.Count > 0) point.Max = maxs.Max();
                break;
            case MetricType.SUM:
                point.Value = contributing.Sum(p => p.Value ?? 0);
                point.Rate = SumOrNull(contributing.Select(p => p.Rate));
                break;
            case MetricType.HISTOGRAM:
            case MetricType.EXPONENTIAL_HISTOGRAM:
                point.Count = contributing.Sum(p => p.Count ?? 0);
                point.Sum = contributing.Sum(p => p.Sum ?? 0);
                point.Rate = SumOrNull(contributing.Select(p => p.Rate));
                var hoMins = contributing.Where(p => p.Min.HasValue).Select(p => p.Min!.Value).ToList();
                if (hoMins.Count > 0) point.Min = hoMins.Min();
                var hoMaxs = contributing.Where(p => p.Max.HasValue).Select(p => p.Max!.Value).ToList();
                if (hoMaxs.Count > 0) point.Max = hoMaxs.Max();
                point.MinMaxApproximate = contributing.Any(p => p.MinMaxApproximate);
                var len = contributing.FirstOrDefault(p => p.BucketCounts != null)?.BucketCounts?.Count;
                if (len is { } l)
                {
                    var merged = new long[l];
                    foreach (var p in contributing)
                        if (p.BucketCounts != null && p.BucketCounts.Count == l)
                            for (var i = 0; i < l; i++) merged[i] += p.BucketCounts[i];
                    point.BucketCounts = merged.ToList();
                    point.BucketBounds = contributing.FirstOrDefault(p => p.BucketBounds != null)?.BucketBounds;
                }
                break;
            case MetricType.SUMMARY:
                if (contributing.Any(p => p.Count.HasValue)) point.Count = contributing.Sum(p => p.Count ?? 0);
                if (contributing.Any(p => p.Sum.HasValue)) point.Sum = contributing.Sum(p => p.Sum ?? 0);
                point.Rate = SumOrNull(contributing.Select(p => p.Rate));
                point.IsApproximate = true;
                var withQuantiles = contributing.Where(p => p.Quantiles is { Count: > 0 }).ToList();
                if (withQuantiles.Count > 0)
                {
                    var qLen = withQuantiles[0].Quantiles!.Count;
                    if (withQuantiles.All(p => p.QuantileValues!.Count == qLen))
                    {
                        point.Quantiles = withQuantiles[0].Quantiles;
                        point.QuantileValues = Enumerable.Range(0, qLen).Select(i => withQuantiles.Average(p => p.QuantileValues![i])).ToList();
                    }
                }
                break;
        }
        return point;
    }

    internal static long[]? DeserializeLongArray(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<long[]>(json);

    internal static double[]? DeserializeDoubleArray(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<double[]>(json);

    internal static T[]? DeserializeArray<T>(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T[]>(json);

    /// <summary>Flattens a data point's raw attributes into a string-keyed label set for series identity.</summary>
    internal static Dictionary<string, string> ToLabelDictionary(Dictionary<string, object>? attributes)
    {
        if (attributes == null || attributes.Count == 0)
            return new Dictionary<string, string>();
        return attributes.ToDictionary(kvp => kvp.Key, kvp => ConvertAttributeValueToString(kvp.Value));
    }

    /// <summary>
    /// Stable identity key for a series: service name plus the label set with keys sorted, so
    /// identical attribute sets across scrapes collapse into one series regardless of ordering.
    /// </summary>
    internal static string SeriesKey(string serviceName, Dictionary<string, string> labels)
    {
        var labelPart = string.Join(",", labels.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return $"{serviceName}{labelPart}";
    }

    internal static string BuildSeriesDisplayName(string serviceName, Dictionary<string, string> labels)
    {
        if (labels.Count == 0) return serviceName;
        var labelPart = string.Join(", ", labels.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return $"{serviceName} | {labelPart}";
    }

    /// <summary>Wide row DTO shared by every Phase 4 bucketed-query projection; a given SQL text only selects the subset of columns it needs, and Dapper leaves the rest at their default.</summary>
    internal sealed class MetricPointRow
    {
        public long MetricId { get; set; }
        public string AttributesJson { get; set; } = "";
        public int Bucket { get; set; }
        public long TimeUnixNano { get; set; }
        public long? StartTimeUnixNano { get; set; }
        public double? ValueDouble { get; set; }
        public long? ValueInt { get; set; }
        public double? AvgValue { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public double? SumOfValue { get; set; }
        /// <summary>Delta loaders: Σ(time − start) over the bucket's points that carry a usable start time, in nanos.</summary>
        public double? CoveredNanos { get; set; }
        public long? Count { get; set; }
        public double? SumValue { get; set; }
        public string? BucketCounts { get; set; }
        public string? ExplicitBounds { get; set; }
        public int? Scale { get; set; }
        public long? ZeroCount { get; set; }
        public int? PositiveOffset { get; set; }
        public string? PositiveBucketCounts { get; set; }
        /// <summary>Maps the <c>quantile_values</c> column (Dapper's underscore-insensitive default name matching).</summary>
        public string? QuantileValues { get; set; }
    }

    /// <summary>Per-stream (metric_id + attributes_json) working state during aggregation.</summary>
    internal sealed class StreamAgg
    {
        public long MetricId { get; set; }
        public string AttributesJson { get; set; } = "";
        public Dictionary<string, object>? Attributes { get; set; }
        public string ServiceName { get; set; } = "unknown";
        /// <summary>Histogram only: this stream's explicit bucket bounds (assumed stable across the window; see decision 42).</summary>
        public double[]? Bounds { get; set; }
        public Dictionary<int, BucketAgg> Buckets { get; } = new();
    }

    /// <summary>One stream's aggregate for one time bucket. Which fields are set depends on the metric type — see the loader that populated it.</summary>
    internal sealed class BucketAgg
    {
        public double? Avg { get; set; }
        public double? Value { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public long? Count { get; set; }
        public double? Sum { get; set; }
        /// <summary>See <see cref="MetricBucketPoint.Rate"/>.</summary>
        public double? Rate { get; set; }
        /// <summary>See <see cref="MetricBucketPoint.MinMaxApproximate"/>.</summary>
        public bool MinMaxApproximate { get; set; }
        ///<summary>Histogram (explicit bounds) merged/deltaed bucket counts, dense, aligned to the stream's <see cref="StreamAgg.Bounds"/>.</summary>
        public long[]? Counts { get; set; }
        /// <summary>Exponential histogram merged/deltaed bucket counts, sparse, keyed by absolute exponent index at the query's global target scale.</summary>
        public Dictionary<long, long>? SparseCounts { get; set; }
        public long? ZeroCountValue { get; set; }
        public List<double>? Quantiles { get; set; }
        public List<double>? QuantileValues { get; set; }
    }
}
