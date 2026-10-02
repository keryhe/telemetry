using System.Security.Cryptography;
using System.Text;
using Keryhe.Telemetry.TestDataGenerator.Clock;
using Keryhe.Telemetry.TestDataGenerator.Config;
using Keryhe.Telemetry.TestDataGenerator.Model;

namespace Keryhe.Telemetry.TestDataGenerator.Topology;

/// <summary>
/// The pods of one tenant. A service has <c>PodsPerService</c> pods at any time; a version change (an
/// incident's bad deploy) replaces them with new pods, as a rollout would, and a rollback brings back the
/// original pods by name, so instance identity is a pure function of (tenant, service, index, version).
/// </summary>
public sealed class InstanceRegistry
{
    private static readonly string[] Regions = ["us-east-1", "eu-west-1", "ap-southeast-2", "us-west-2"];

    private readonly string _tenant;
    private readonly GeneratorOptions _options;
    private readonly IncidentSchedule _incidents;
    private readonly Dictionary<(string Service, int Index, string Version), ServiceInstance> _cache = [];
    private readonly string _region;

    public InstanceRegistry(string tenant, GeneratorOptions options, IncidentSchedule incidents)
    {
        _tenant = tenant;
        _options = options;
        _incidents = incidents;
        _region = Regions[(uint)SimRandom.Hash(tenant, "region") % Regions.Length];
    }

    public string VersionAt(string service, DateTimeOffset t) =>
        _incidents.VersionOverride(service, t) ?? Services.BaselineVersions[service];

    public ServiceInstance Get(string service, int index, string version)
    {
        var key = (service, index, version);
        if (_cache.TryGetValue(key, out var existing)) return existing;

        var replicaSet = Base36((uint)SimRandom.Hash("rs", service, version), 10);
        var suffix = Base36((uint)SimRandom.Hash(_tenant, service, index, version), 5);
        var pod = $"{service}-{replicaSet}-{suffix}";
        var node = SimRandom.Hash(_tenant, "node", service, index);
        var instance = new ServiceInstance(
            Tenant: _tenant,
            Service: service,
            Version: version,
            PodName: pod,
            InstanceId: Uid($"{_tenant}/{pod}"),
            HostName: $"ip-10-{(node >> 8) & 0xFF}-{(node >> 16) & 0xFF}-{node & 0xFF}.{_region}.compute.internal",
            Region: _region,
            Namespace: "shop",
            Environment: _options.Environment);
        _cache[key] = instance;
        return instance;
    }

    /// <summary>The pods of <paramref name="service"/> that exist at <paramref name="t"/>.</summary>
    public IEnumerable<ServiceInstance> ActiveAt(string service, DateTimeOffset t)
    {
        var version = VersionAt(service, t);
        for (var i = 0; i < _options.PodsPerService; i++)
            yield return Get(service, i, version);
    }

    public IEnumerable<ServiceInstance> ActiveAt(DateTimeOffset t) => Services.All.SelectMany(s => ActiveAt(s, t));

    public ServiceInstance Pick(string service, DateTimeOffset t, SimRandom rng) =>
        Get(service, rng.Next(_options.PodsPerService), VersionAt(service, t));

    private static string Base36(uint value, int length)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        var chars = new char[length];
        var v = (ulong)value * 2654435761UL;
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[(int)(v % 36)];
            v = v / 36 + (ulong)value * 40503UL + (ulong)i;
        }
        return new string(chars);
    }

    private static string Uid(string seed) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(seed))).ToString();
}
