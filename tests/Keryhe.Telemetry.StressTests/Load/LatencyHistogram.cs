namespace Keryhe.Telemetry.StressTests.Load;

/// <summary>
/// Thread-safe log-scale latency histogram, ~5% relative precision from 10 microseconds to hours.
/// Percentiles report a bucket's upper bound, so they never understate.
/// </summary>
public sealed class LatencyHistogram
{
    private const double MinMs = 0.01;
    private const double Growth = 1.05;
    private static readonly double LogGrowth = Math.Log(Growth);
    private const int BucketCount = 500;

    private readonly long[] _buckets = new long[BucketCount];
    private readonly object _gate = new();
    private long _count;
    private double _sum;
    private double _max;

    public void Record(double milliseconds)
    {
        var index = milliseconds <= MinMs ? 0 : Math.Min(BucketCount - 1, (int)Math.Ceiling(Math.Log(milliseconds / MinMs) / LogGrowth));
        lock (_gate)
        {
            _buckets[index]++;
            _count++;
            _sum += milliseconds;
            if (milliseconds > _max) _max = milliseconds;
        }
    }

    public LatencySummary Summarize()
    {
        lock (_gate)
        {
            if (_count == 0) return new LatencySummary(0, 0, 0, 0, 0, 0);
            return new LatencySummary(_count, _sum / _count, Percentile(0.50), Percentile(0.95), Percentile(0.99), _max);
        }
    }

    // Caller holds _gate.
    private double Percentile(double q)
    {
        var rank = (long)Math.Ceiling(q * _count);
        long seen = 0;
        for (var i = 0; i < BucketCount; i++)
        {
            seen += _buckets[i];
            if (seen >= rank)
                return Math.Min(_max, MinMs * Math.Pow(Growth, i));
        }
        return _max;
    }
}

public sealed record LatencySummary(long Count, double MeanMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs);
