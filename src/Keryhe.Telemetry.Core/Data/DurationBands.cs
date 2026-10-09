using System.Numerics;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// The 24 doubling duration bands of the request rollup (summary-rollups plan, "Duration bands"):
/// band 0 is under 0.25 ms, band N (1-22) is [0.25 ms * 2^(N-1), 0.25 ms * 2^N), and band 23 is
/// 1,048.6 s and over. <see cref="IndexOf"/> is the one definition; ClickHouse's writer
/// computes the same with integer arithmetic and a test checks both at every edge +-1 ns.
/// </summary>
public static class DurationBands
{
    public const int Count = 24;

    /// <summary>Upper edge of band 0 (0.25 ms) in nanoseconds.</summary>
    public const long FirstEdgeNanos = 250_000;

    /// <summary>Band holding <paramref name="nanos"/>. A negative duration (clock skew) counts as 0.</summary>
    public static int IndexOf(long nanos)
    {
        if (nanos < FirstEdgeNanos) return 0;
        // floor(log2(nanos / edge)) + 1 == the bit length of the integer quotient.
        var quotient = (ulong)(nanos / FirstEdgeNanos);
        return Math.Min(Count - 1, 64 - BitOperations.LeadingZeroCount(quotient));
    }

    /// <summary>Inclusive lower edge of <paramref name="band"/> in nanoseconds (0 for band 0).</summary>
    public static long LowerEdgeNanos(int band)
        => band <= 0 ? 0 : FirstEdgeNanos << (band - 1);

    /// <summary>Exclusive upper edge of <paramref name="band"/> in nanoseconds; <see cref="long.MaxValue"/> for the open last band.</summary>
    public static long UpperEdgeNanos(int band)
        => band >= Count - 1 ? long.MaxValue : FirstEdgeNanos << band;

    /// <summary>
    /// Percentile <paramref name="p"/> (0-1) in nanoseconds from per-band counts: finds the band
    /// holding rank <c>p * total</c>, interpolates geometrically between its edges (linearly from 0
    /// in band 0, and assuming a doubling width for the open last band), and clamps to
    /// <paramref name="maxNanos"/>. Returns 0 for an empty histogram. Off by up to the band width
    /// (2x) in the worst case.
    /// </summary>
    public static double Percentile(IReadOnlyList<long> bands, double p, long maxNanos)
    {
        long total = 0;
        foreach (var c in bands) total += c;
        if (total <= 0) return 0;

        var target = Math.Clamp(p, 0, 1) * total;
        long before = 0;
        for (var band = 0; band < bands.Count; band++)
        {
            var count = bands[band];
            if (count <= 0) continue;
            if (before + count >= target || band == bands.Count - 1)
            {
                var fraction = Math.Clamp((target - before) / count, 0, 1);
                var lower = (double)LowerEdgeNanos(band);
                var upper = band >= Count - 1 ? lower * 2 : (double)UpperEdgeNanos(band);
                var value = band == 0
                    ? upper * fraction
                    : lower * Math.Pow(upper / lower, fraction);
                return maxNanos > 0 ? Math.Min(value, maxNanos) : value;
            }
            before += count;
        }
        return 0;
    }
}
