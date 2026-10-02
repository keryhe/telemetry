namespace Keryhe.Telemetry.TestDataGenerator.Clock;

/// <summary>Request rate over time: a daily curve, a weekend lift and per-minute burstiness.</summary>
public sealed class TrafficCurve
{
    private readonly string _tenant;
    private readonly int _seed;

    public TrafficCurve(string tenant, int seed)
    {
        _tenant = tenant;
        _seed = seed;
    }

    /// <summary>0.1 overnight to 1.0 at the evening peak (20:00 UTC).</summary>
    public static double Daily(DateTimeOffset t)
    {
        var hour = t.UtcDateTime.TimeOfDay.TotalHours;
        return 0.55 + 0.45 * Math.Cos(2 * Math.PI * (hour - 20) / 24);
    }

    public static double Weekday(DateTimeOffset t) => t.UtcDateTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1.15 : 1.0;

    /// <summary>
    /// A per-minute multiplier around 1.0 (log-normal, sigma 0.2) with the occasional 2-3x burst. Derived
    /// from the minute alone, so any chunking of time sees the same traffic.
    /// </summary>
    public double Burst(DateTimeOffset t)
    {
        var minute = t.ToUnixTimeSeconds() / 60;
        var rng = new SimRandom(SimRandom.Hash(_seed, _tenant, "burst", minute));
        var factor = rng.LogNormal(1.0, 0.2);
        if (rng.Chance(0.03)) factor *= rng.Uniform(2.0, 3.0);
        return factor;
    }

    /// <summary>Expected top-level requests per second at <paramref name="t"/>.</summary>
    public double Rate(DateTimeOffset t, double peakRequestsPerSecond, double scale) =>
        peakRequestsPerSecond * scale * Daily(t) * Weekday(t) * Burst(t);
}
