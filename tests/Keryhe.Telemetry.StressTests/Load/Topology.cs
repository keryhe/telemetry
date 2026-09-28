using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace Keryhe.Telemetry.StressTests.Load;

/// <summary>A seeded tenant the load tool may send as: its id (for the ledger) and plaintext API key.</summary>
public sealed record LoadTenant(long Id, string Name, string ApiKey);

public sealed class ServiceInfo(string name, Resource resource, string[] operations)
{
    public string Name { get; } = name;
    public Resource Resource { get; } = resource;
    public string[] Operations { get; } = operations;
}

/// <summary>
/// The fixed cast of services and operations per tenant, built once from the profile. Resources are
/// built once and shared across requests, so the server sees a small, stable set of distinct resources
/// (its resource dedup key is the attribute map) rather than one per export.
/// </summary>
public sealed class Topology
{
    public IReadOnlyList<LoadTenant> Tenants { get; }
    public IReadOnlyList<ServiceInfo[]> ServicesByTenant { get; }
    public double[] CumulativeWeights { get; }
    public InstrumentationScope Scope { get; } = new() { Name = "stress.load", Version = "1.0.0" };

    public Topology(IReadOnlyList<LoadTenant> tenants, LoadProfile profile)
    {
        Tenants = tenants;
        ServicesByTenant = tenants.Select((t, ti) =>
            Enumerable.Range(0, Math.Max(1, profile.ServicesPerTenant)).Select(si =>
            {
                var name = $"svc-{ti}-{si}";
                var resource = new Resource();
                resource.Attributes.Add(Str("service.name", name));
                resource.Attributes.Add(Str("service.namespace", $"tenant-{ti}"));
                resource.Attributes.Add(Str("deployment.environment", "stress"));
                var ops = Enumerable.Range(0, Math.Max(1, profile.OperationsPerService)).Select(o => $"op-{si}-{o}").ToArray();
                return new ServiceInfo(name, resource, ops);
            }).ToArray()).ToList();

        var running = 0.0;
        CumulativeWeights = tenants.Select((_, i) => running += i < profile.TenantWeights.Count ? Math.Max(0, profile.TenantWeights[i]) : 1).ToArray();
    }

    public int PickTenant(Random rng)
    {
        var total = CumulativeWeights[^1];
        if (total <= 0) return rng.Next(Tenants.Count);
        var x = rng.NextDouble() * total;
        for (var i = 0; i < CumulativeWeights.Length; i++)
            if (x < CumulativeWeights[i]) return i;
        return CumulativeWeights.Length - 1;
    }

    public static KeyValue Str(string key, string value) => new() { Key = key, Value = new AnyValue { StringValue = value } };
    public static KeyValue Int(string key, long value) => new() { Key = key, Value = new AnyValue { IntValue = value } };

    public static ByteString RandomId(Random rng, int bytes)
    {
        var buffer = new byte[bytes];
        do rng.NextBytes(buffer); while (buffer.All(b => b == 0));
        return ByteString.CopyFrom(buffer);
    }
}

/// <summary>Timestamps: now, a few minutes late, or backdated past retention, per the profile's time shaping.</summary>
public static class TimeStamps
{
    public static long NowNanos() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

    /// <summary>Picks the base time for one record and reports which age bucket it falls in.</summary>
    public static (long Nanos, RecordAge Age) Pick(TimeShaping shaping, Random rng)
    {
        var now = NowNanos();
        var roll = rng.NextDouble();
        if (roll < shaping.BackdatedFraction)
        {
            var ageDays = shaping.BackdatedAgeDays.Sample(rng);
            return (now - ageDays * 86_400_000_000_000L - (long)(rng.NextDouble() * 3_600_000_000_000L), RecordAge.Backdated);
        }
        if (roll < shaping.BackdatedFraction + shaping.LateArrivalFraction)
            return (now - shaping.LateArrivalMinutes.Sample(rng) * 60_000_000_000L, RecordAge.Current);
        return (now, RecordAge.Current);
    }
}

/// <summary>What a shaper hands the exporter: the request, its tenant and the ledger rows it carries.</summary>
public sealed record Payload<TRequest>(TRequest Request, int TenantIndex, int Records, IReadOnlyList<LedgerEntry> Entries, bool Redeliver);
