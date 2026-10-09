using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Plans a rollup window (whole minutes, a bucket width from a fixed ladder, clamped to what is
/// written) and folds the rows a repository returns into the API responses
/// (plans/summary-rollups.md, decisions b and d). Pure, so it is unit-tested without a database.
/// </summary>
public static class RollupSummaryBuilder
{
    private const long MinuteNanos = 60_000_000_000L;
    private const long SecondNanos = 1_000_000_000L;

    /// <summary>Bucket widths in minutes: 1, 2, 5, 10, 15, 30 min, 1, 2, 3, 6, 12 h, 1 d. Every width divides a day, and those of an hour or more are whole hours.</summary>
    public static readonly int[] LadderMinutes = [1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440];

    /// <summary>The planned window: whole-minute <c>[StartNano, EndNano)</c> and the bucket width.</summary>
    public readonly record struct Window(long StartNano, long EndNano, long BucketSeconds)
    {
        public bool IsEmpty => EndNano <= StartNano;
    }

    /// <summary>
    /// <c>writtenThrough</c>: now minus <c>CloseGraceSeconds</c>, <c>FlushIntervalSeconds</c> and
    /// <c>ArrivalMarginSeconds</c>, rounded down to the minute (decision d).
    /// </summary>
    public static DateTime WrittenThrough(DateTime nowUtc, RollupOptions options)
    {
        var nanos = TimeConversion.DateTimeToUnixNano(nowUtc)
                    - (options.CloseGraceSeconds + options.FlushIntervalSeconds + options.ArrivalMarginSeconds) * SecondNanos;
        return TimeConversion.UnixNanoToDateTime(FloorTo(nanos, MinuteNanos));
    }

    /// <summary>
    /// Rounds the start down and the end up to whole minutes, clamps the end to <paramref name="writtenThrough"/>
    /// and picks the smallest ladder width giving at most <paramref name="bucketCount"/> buckets over that span
    /// (the widest when none does). An unaligned window gets partial first and last buckets, so a result can
    /// hold <c>bucketCount + 1</c> of them.
    /// </summary>
    public static Window PlanWindow(DateTime start, DateTime end, DateTime writtenThrough, int bucketCount)
    {
        var startNano = FloorTo(TimeConversion.DateTimeToUnixNano(start), MinuteNanos);
        var endNano = CeilTo(TimeConversion.DateTimeToUnixNano(end), MinuteNanos);
        endNano = Math.Min(endNano, TimeConversion.DateTimeToUnixNano(writtenThrough));
        var span = Math.Max(0, endNano - startNano);

        var target = Math.Max(1, bucketCount);
        var width = LadderMinutes[^1];
        foreach (var minutes in LadderMinutes)
        {
            var buckets = (span + minutes * MinuteNanos - 1) / (minutes * MinuteNanos);
            if (buckets <= target) { width = minutes; break; }
        }
        return new Window(startNano, Math.Max(startNano, endNano), width * 60L);
    }

    private static IEnumerable<(long Start, double Covered)> Buckets(Window window)
    {
        var width = window.BucketSeconds * SecondNanos;
        for (var bucket = FloorTo(window.StartNano, width); bucket < window.EndNano; bucket += width)
        {
            var from = Math.Max(bucket, window.StartNano);
            var to = Math.Min(bucket + width, window.EndNano);
            yield return (bucket, (to - from) / (double)SecondNanos);
        }
    }

    public static RequestSummaryResult BuildRequestSummary(
        Window window, IReadOnlyList<RequestRollupAggregate> rows, DateTime writtenThrough, bool timedOut = false)
    {
        if (timedOut)
            return new RequestSummaryResult { BucketSeconds = (int)window.BucketSeconds, WrittenThrough = writtenThrough, TimedOut = true };

        var byBucket = rows.GroupBy(r => r.BucketStartNano).ToDictionary(g => g.Key, g => g.ToList());
        var buckets = new List<RequestVolumeBucket>();
        var latency = new List<RequestLatencyCell>();
        var width = window.BucketSeconds * SecondNanos;
        double coveredTotal = 0;

        foreach (var (start, covered) in Buckets(window))
        {
            coveredTotal += covered;
            byBucket.TryGetValue(start, out var inBucket);
            var total = Sum(inBucket ?? []);
            var startTime = TimeConversion.UnixNanoToDateTime(start);
            buckets.Add(new RequestVolumeBucket
            {
                Timestamp = startTime,
                CoveredSeconds = covered,
                Count = total.Count,
                ErrorCount = total.Errors,
                SumDurationMs = total.SumNanos / 1e6,
                MaxDurationMs = total.MaxNanos / 1e6,
                P50Ms = DurationBands.Percentile(total.Bands, 0.50, total.MaxNanos) / 1e6,
                P95Ms = DurationBands.Percentile(total.Bands, 0.95, total.MaxNanos) / 1e6,
                P99Ms = DurationBands.Percentile(total.Bands, 0.99, total.MaxNanos) / 1e6
            });

            var endTime = TimeConversion.UnixNanoToDateTime(start + width);
            for (var band = 0; band < total.Bands.Length; band++)
            {
                if (total.Bands[band] == 0) continue;
                var lower = DurationBands.LowerEdgeNanos(band);
                var upper = band == DurationBands.Count - 1 ? lower * 2 : DurationBands.UpperEdgeNanos(band);
                latency.Add(new RequestLatencyCell
                {
                    XStart = startTime, XEnd = endTime, Band = band,
                    YStartMs = lower / 1e6, YEndMs = upper / 1e6, Count = total.Bands[band]
                });
            }
        }

        var all = Sum(rows);
        var services = rows.GroupBy(r => r.Service).Select(g =>
        {
            var s = Sum(g);
            return new ServiceStats
            {
                Service = g.Key,
                Count = (int)Math.Min(int.MaxValue, s.Count),
                ErrorCount = (int)Math.Min(int.MaxValue, s.Errors),
                ErrorRate = s.Count == 0 ? 0 : 100.0 * s.Errors / s.Count,
                RatePerSecond = coveredTotal > 0 ? s.Count / coveredTotal : 0,
                AvgMs = s.Count == 0 ? 0 : s.SumNanos / (double)s.Count / 1e6,
                P50Ms = DurationBands.Percentile(s.Bands, 0.50, s.MaxNanos) / 1e6,
                P90Ms = DurationBands.Percentile(s.Bands, 0.90, s.MaxNanos) / 1e6,
                P95Ms = DurationBands.Percentile(s.Bands, 0.95, s.MaxNanos) / 1e6
            };
        }).OrderByDescending(s => s.Count).ThenBy(s => s.Service, StringComparer.Ordinal).ToList();

        return new RequestSummaryResult
        {
            BucketSeconds = (int)window.BucketSeconds,
            WrittenThrough = writtenThrough,
            Summary = new RequestWindowSummary
            {
                Count = all.Count,
                ErrorCount = all.Errors,
                AvgMs = all.Count == 0 ? 0 : all.SumNanos / (double)all.Count / 1e6,
                MaxMs = all.MaxNanos / 1e6,
                P50Ms = DurationBands.Percentile(all.Bands, 0.50, all.MaxNanos) / 1e6,
                P95Ms = DurationBands.Percentile(all.Bands, 0.95, all.MaxNanos) / 1e6,
                P99Ms = DurationBands.Percentile(all.Bands, 0.99, all.MaxNanos) / 1e6,
                RatePerSecond = coveredTotal > 0 ? all.Count / coveredTotal : 0
            },
            Buckets = buckets,
            Services = services,
            Latency = latency
        };
    }

    public static LogRollupSummaryResult BuildLogSummary(
        Window window, IReadOnlyList<LogRollupAggregate> rows, DateTime writtenThrough, bool timedOut = false)
    {
        if (timedOut)
            return new LogRollupSummaryResult { BucketSeconds = (int)window.BucketSeconds, WrittenThrough = writtenThrough, TimedOut = true };

        var byBucket = rows.GroupBy(r => r.BucketStartNano).ToDictionary(g => g.Key, g => g.ToList());
        var buckets = new List<LogRollupBucket>();
        foreach (var (start, covered) in Buckets(window))
        {
            long trace = 0, debug = 0, info = 0, warn = 0, error = 0, fatal = 0;
            if (byBucket.TryGetValue(start, out var inBucket))
                foreach (var row in inBucket)
                    switch (SeverityGroup(row.SeverityNumber))
                    {
                        case 0: trace += row.RecordCount; break;
                        case 1: debug += row.RecordCount; break;
                        case 2: info += row.RecordCount; break;
                        case 3: warn += row.RecordCount; break;
                        case 4: error += row.RecordCount; break;
                        default: fatal += row.RecordCount; break;
                    }
            buckets.Add(new LogRollupBucket
            {
                Timestamp = TimeConversion.UnixNanoToDateTime(start), CoveredSeconds = covered,
                Trace = trace, Debug = debug, Info = info, Warn = warn, Error = error, Fatal = fatal
            });
        }
        return new LogRollupSummaryResult
        {
            BucketSeconds = (int)window.BucketSeconds, WrittenThrough = writtenThrough,
            Total = rows.Sum(r => r.RecordCount), Buckets = buckets
        };
    }

    /// <summary>
    /// The six severity groups (0 trace, 1 debug, 2 info, 3 warn, 4 error, 5 fatal), with the rollup's -1 for
    /// "no severity" counted as Info first: no severity is Info, while the Trace group is <c>&lt;= 4</c>, which -1
    /// would otherwise fall into.
    /// </summary>
    public static int SeverityGroup(int severityNumber) => severityNumber switch
    {
        -1 => 2,
        <= 4 => 0,
        <= 8 => 1,
        <= 12 => 2,
        <= 16 => 3,
        <= 20 => 4,
        _ => 5
    };

    private static (long Count, long Errors, long SumNanos, long MaxNanos, long[] Bands) Sum(IEnumerable<RequestRollupAggregate> rows)
    {
        long count = 0, errors = 0, sum = 0, max = 0;
        var bands = new long[DurationBands.Count];
        foreach (var r in rows)
        {
            count += r.RequestCount;
            errors += r.ErrorCount;
            sum += r.SumDurationNanos;
            if (r.MaxDurationNanos > max) max = r.MaxDurationNanos;
            for (var i = 0; i < bands.Length && i < r.Bands.Length; i++) bands[i] += r.Bands[i];
        }
        return (count, errors, sum, max, bands);
    }

    private static long FloorTo(long value, long unit)
    {
        var r = value % unit;
        return r < 0 ? value - r - unit : value - r;
    }

    private static long CeilTo(long value, long unit)
    {
        var floor = FloorTo(value, unit);
        return floor == value ? value : floor + unit;
    }
}
