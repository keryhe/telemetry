using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Collector.Authentication;

/// <summary>Limits failed authentication attempts per client address, bound from <c>Telemetry:Collector:AuthFailureLimit</c>.</summary>
public sealed class AuthFailureLimitOptions
{
    /// <summary>Failed attempts per second a client address earns back (a token bucket). 0 turns the limit off.</summary>
    public double PerSecond { get; set; } = 5;

    /// <summary>The most failed attempts in a burst before a client address is refused.</summary>
    public int Burst { get; set; } = 20;

    /// <summary>
    /// Client addresses tracked at once; past it, new addresses share one overflow bucket, so the limiter's own memory cannot be
    /// grown by an attacker spraying addresses.
    /// </summary>
    public int MaxTrackedClients { get; set; } = 100_000;

    public void Validate()
    {
        if (PerSecond < 0) throw new InvalidOperationException($"Telemetry:Collector:AuthFailureLimit:PerSecond must not be negative (was {PerSecond}).");
        if (Burst < 1) throw new InvalidOperationException($"Telemetry:Collector:AuthFailureLimit:Burst must be at least 1 (was {Burst}).");
        if (MaxTrackedClients < 1) throw new InvalidOperationException($"Telemetry:Collector:AuthFailureLimit:MaxTrackedClients must be at least 1 (was {MaxTrackedClients}).");
    }
}

/// <summary>
/// A token bucket of failed authentication attempts per client address. Only failures (a missing, malformed, invalid or expired key) take
/// a token; a client with none left is refused before any control-plane lookup for a key that is not already cached, and a key in the
/// positive cache is never refused (the handler checks that first), so a valid client behind the same NAT as a misconfigured one keeps
/// working. IPv6 addresses are grouped by /64, the smallest block one subscriber normally owns. Per-address state is bounded
/// (<see cref="AuthFailureLimitOptions.MaxTrackedClients"/>, idle buckets are dropped).
/// </summary>
public sealed class AuthFailureLimiter(IOptions<TelemetryCollectorOptions> options, TimeProvider time)
{
    private sealed class Bucket { public double Tokens; public long Ticks; }

    private const string Overflow = "*overflow*";

    private readonly AuthFailureLimitOptions _limit = options.Value.AuthFailureLimit;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private long _nextSweep;

    public bool Enabled => _limit.PerSecond > 0;

    /// <summary>Whether <paramref name="address"/> has used up its failed attempts and has not earned one back yet.</summary>
    public bool IsExhausted(IPAddress? address)
    {
        if (!Enabled) return false;
        var bucket = BucketFor(address);
        lock (bucket)
        {
            Refill(bucket);
            return bucket.Tokens < 1;
        }
    }

    /// <summary>Counts a failed attempt from <paramref name="address"/>.</summary>
    public void RecordFailure(IPAddress? address)
    {
        if (!Enabled) return;
        var bucket = BucketFor(address);
        lock (bucket)
        {
            Refill(bucket);
            bucket.Tokens = Math.Max(0, bucket.Tokens - 1);
        }
    }

    /// <summary>Addresses currently tracked (for tests).</summary>
    public int Tracked => _buckets.Count;

    private Bucket BucketFor(IPAddress? address)
    {
        var now = time.GetTimestamp();
        Sweep(now);
        var key = KeyOf(address);
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            if (_buckets.Count >= _limit.MaxTrackedClients) key = Overflow;
            bucket = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = _limit.Burst, Ticks = now });
        }
        return bucket;
    }

    private void Refill(Bucket b)
    {
        var now = time.GetTimestamp();
        b.Tokens = Math.Min(_limit.Burst, b.Tokens + time.GetElapsedTime(b.Ticks, now).TotalSeconds * _limit.PerSecond);
        b.Ticks = now;
    }

    // A bucket that would be full again carries no information; dropping it keeps memory to the clients that are actually failing.
    private void Sweep(long now)
    {
        var next = Interlocked.Read(ref _nextSweep);
        if (now < next) return;
        if (Interlocked.CompareExchange(ref _nextSweep, now + time.TimestampFrequency * 30, next) != next) return;

        var refill = _limit.Burst / _limit.PerSecond;
        foreach (var (key, bucket) in _buckets)
            lock (bucket)
                if (key != Overflow && time.GetElapsedTime(bucket.Ticks, now).TotalSeconds >= refill)
                    _buckets.TryRemove(key, out _);
    }

    private static string KeyOf(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();
        return Convert.ToHexString(address.GetAddressBytes().AsSpan(0, 8));
    }
}
