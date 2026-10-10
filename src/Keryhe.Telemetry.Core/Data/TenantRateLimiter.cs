using System.Diagnostics;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// A token bucket per (tenant, signal) in records per second, with a burst of one second's worth. An export larger than the burst is
/// charged the whole burst (it needs a full bucket) rather than being impossible to admit. Thread-safe; buckets idle for a minute
/// are dropped.
/// </summary>
public sealed class TenantRateLimiter
{
    private sealed class Bucket { public double Tokens; public long LastTicks; public double Rate; }

    private readonly Dictionary<(long Tenant, string Signal), Bucket> _buckets = [];
    private readonly object _sync = new();
    private long _lastSweep = Stopwatch.GetTimestamp();

    /// <summary>Takes <paramref name="count"/> tokens; false with the wait until they are available in <paramref name="retryAfter"/>.</summary>
    public bool TryAcquire(long tenantId, string signal, int count, double ratePerSecond, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (ratePerSecond <= 0 || count <= 0) return true;

        var now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            if (Stopwatch.GetElapsedTime(_lastSweep, now).TotalSeconds > 60) Sweep(now);

            if (!_buckets.TryGetValue((tenantId, signal), out var b))
                _buckets[(tenantId, signal)] = b = new Bucket { Tokens = ratePerSecond, LastTicks = now, Rate = ratePerSecond };

            // A changed rate (a reloaded override) takes effect at once; the bucket never holds more than one second of it.
            b.Rate = ratePerSecond;
            b.Tokens = Math.Min(ratePerSecond, b.Tokens + Stopwatch.GetElapsedTime(b.LastTicks, now).TotalSeconds * ratePerSecond);
            b.LastTicks = now;

            var cost = Math.Min(count, ratePerSecond);
            if (b.Tokens >= cost)
            {
                b.Tokens -= cost;
                return true;
            }
            retryAfter = TimeSpan.FromSeconds((cost - b.Tokens) / ratePerSecond);
            return false;
        }
    }

    private void Sweep(long now)
    {
        _lastSweep = now;
        foreach (var key in _buckets.Where(kv => Stopwatch.GetElapsedTime(kv.Value.LastTicks, now).TotalSeconds > 60).Select(kv => kv.Key).ToList())
            _buckets.Remove(key);
    }
}
