namespace Keryhe.Telemetry.TestDataGenerator.Clock;

/// <summary>A seeded random source with the distributions the simulator needs.</summary>
public sealed class SimRandom
{
    private readonly Random _random;

    public SimRandom(int seed) => _random = new Random(seed);

    /// <summary>A stable (process-independent) hash; <c>string.GetHashCode</c> is randomized per process.</summary>
    public static int Hash(params object[] parts)
    {
        unchecked
        {
            var h = (ulong)14695981039346656037;
            foreach (var p in parts)
            {
                foreach (var c in p.ToString() ?? "")
                {
                    h ^= c;
                    h *= 1099511628211;
                }
                h ^= 0xFF;
                h *= 1099511628211;
            }
            return (int)(h ^ (h >> 32));
        }
    }

    public double NextDouble() => _random.NextDouble();

    public int Next(int maxExclusive) => _random.Next(maxExclusive);

    public int Next(int minInclusive, int maxExclusive) => _random.Next(minInclusive, maxExclusive);

    public double Uniform(double min, double max) => min + _random.NextDouble() * (max - min);

    public bool Chance(double p) => p > 0 && _random.NextDouble() < p;

    public T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

    public T Pick<T>(params T[] items) => items[_random.Next(items.Length)];

    public T Weighted<T>(IReadOnlyList<(T Item, double Weight)> items)
    {
        var total = items.Sum(i => i.Weight);
        var roll = _random.NextDouble() * total;
        foreach (var (item, weight) in items)
        {
            roll -= weight;
            if (roll <= 0) return item;
        }
        return items[^1].Item;
    }

    public double Normal()
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = 1.0 - _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>A right-skewed value with the given median; <paramref name="sigma"/> is the log-space spread.</summary>
    public double LogNormal(double median, double sigma = 0.45) => median * Math.Exp(sigma * Normal());

    /// <summary>Poisson draw (Knuth for small means, a normal approximation above 30).</summary>
    public int Poisson(double lambda)
    {
        if (lambda <= 0) return 0;
        if (lambda > 30) return Math.Max(0, (int)Math.Round(lambda + Math.Sqrt(lambda) * Normal()));
        var l = Math.Exp(-lambda);
        var k = 0;
        var p = 1.0;
        do
        {
            k++;
            p *= _random.NextDouble();
        } while (p > l);
        return k - 1;
    }

    public string Hex(int bytes)
    {
        Span<byte> buffer = stackalloc byte[bytes];
        _random.NextBytes(buffer);
        // An all-zero id is invalid in OTLP.
        if (buffer.IndexOfAnyExcept((byte)0) < 0) buffer[0] = 1;
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }

    public string TraceId() => Hex(16);

    public string SpanId() => Hex(8);
}
