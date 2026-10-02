using Keryhe.Telemetry.TestDataGenerator.Clock;
using Keryhe.Telemetry.TestDataGenerator.Config;
using Keryhe.Telemetry.TestDataGenerator.Flows;
using Keryhe.Telemetry.TestDataGenerator.Model;
using Keryhe.Telemetry.TestDataGenerator.Topology;

namespace Keryhe.Telemetry.TestDataGenerator;

/// <summary>
/// Simulates one tenant's shop. Given a stretch of virtual time it returns what the tenant's services
/// emitted in it. Every random decision is seeded from (seed, tenant, time), so the same time range always
/// yields the same telemetry however it is split into chunks. Not thread-safe; one instance per tenant.
/// </summary>
public sealed class TenantSimulator
{
    private static readonly Dictionary<string, double> CpuPerRequest = new()
    {
        [Services.Frontend] = 0.06, [Services.Gateway] = 0.05, [Services.User] = 0.03, [Services.Catalog] = 0.04,
        [Services.Cart] = 0.02, [Services.Order] = 0.08, [Services.Inventory] = 0.04, [Services.Payment] = 0.05,
        [Services.Notification] = 0.03,
    };

    private static readonly Dictionary<string, double> BaseMemoryMb = new()
    {
        [Services.Frontend] = 310, [Services.Gateway] = 190, [Services.User] = 170, [Services.Catalog] = 260,
        [Services.Cart] = 150, [Services.Order] = 280, [Services.Inventory] = 175, [Services.Payment] = 185,
        [Services.Notification] = 160,
    };

    private readonly TenantOptions _tenant;
    private readonly GeneratorOptions _options;
    private readonly SimEnvironment _env;
    private readonly TrafficCurve _curve;
    private readonly HashSet<string> _started = [];
    private readonly Dictionary<string, int> _requestCounts = [];
    private DateTimeOffset? _lastSample;

    public TenantSimulator(TenantOptions tenant, GeneratorOptions options)
    {
        _tenant = tenant;
        _options = options;
        var incidents = new IncidentSchedule(tenant.Name, options.Incidents);
        _env = new SimEnvironment
        {
            Tenant = tenant.Name,
            Options = options,
            Incidents = incidents,
            Instances = new InstanceRegistry(tenant.Name, options, incidents),
        };
        _curve = new TrafficCurve(tenant.Name, options.Seed);
    }

    public string Name => _tenant.Name;

    public InstanceRegistry Instances => _env.Instances;

    /// <summary>Produces everything that began in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public SimChunk Simulate(DateTimeOffset from, DateTimeOffset to, bool includeSamples)
    {
        var roots = new List<SimSpan>();
        var background = new List<SimLog>();

        for (var t = from; t < to;)
        {
            var secondStart = DateTimeOffset.FromUnixTimeSeconds(t.ToUnixTimeSeconds());
            var windowEnd = Min(to, secondStart.AddSeconds(1));
            var seconds = (windowEnd - t).TotalSeconds;
            var rng = new SimRandom(SimRandom.Hash(_options.Seed, _tenant.Name, "second", secondStart.ToUnixTimeSeconds()));

            GenerateRequests(rng, t, windowEnd, seconds, roots);
            GenerateBackgroundLogs(rng, t, windowEnd, seconds, background);
            t = windowEnd;
        }

        foreach (var span in roots.SelectMany(r => r.Descendants()))
        {
            if (span.Kind is SimSpanKind.Server or SimSpanKind.Consumer)
                _requestCounts[span.Instance.PodName] = _requestCounts.GetValueOrDefault(span.Instance.PodName) + 1;
        }

        roots.Sort((a, b) => a.Start.CompareTo(b.Start));
        background.Sort((a, b) => a.Time.CompareTo(b.Time));
        var samples = includeSamples ? TakeSamples(to) : [];
        return new SimChunk { Tenant = _tenant.Name, From = from, To = to, Traces = roots, BackgroundLogs = background, Samples = samples };
    }

    private void GenerateRequests(SimRandom rng, DateTimeOffset from, DateTimeOffset to, double seconds, List<SimSpan> roots)
    {
        var rate = _curve.Rate(from + (to - from) / 2, _options.PeakRequestsPerSecond, _tenant.Scale);
        var count = rng.Poisson(rate * seconds);
        var span = (to - from).TotalSeconds;
        var weighted = Journeys.All.Select(j => (j, j.Weight)).ToList();
        for (var i = 0; i < count; i++)
        {
            var start = from + TimeSpan.FromSeconds(rng.Uniform(0, span));
            var journey = rng.Weighted(weighted);
            journey.Run(new FlowTrace(_env, rng, roots), start);
        }
    }

    private void GenerateBackgroundLogs(SimRandom rng, DateTimeOffset from, DateTimeOffset to, double seconds, List<SimLog> output)
    {
        foreach (var instance in _env.Instances.ActiveAt(from))
        {
            if (_started.Add(instance.PodName))
            {
                foreach (var (category, body) in new[]
                {
                    ("Microsoft.Hosting.Lifetime", "Now listening on: http://[::]:8080"),
                    ("Microsoft.Hosting.Lifetime", "Application started. Press Ctrl+C to shut down."),
                    ("Microsoft.Hosting.Lifetime", $"Hosting environment: {char.ToUpperInvariant(_options.Environment[0])}{_options.Environment[1..]}"),
                    ("Microsoft.Hosting.Lifetime", "Content root path: /app"),
                })
                {
                    output.Add(new SimLog { Time = from, Severity = SimSeverity.Info, Body = body, Instance = instance, Category = category });
                }
                output.Add(new SimLog
                {
                    Time = from, Severity = SimSeverity.Info, Instance = instance, Category = Names.Category(instance.Service),
                    Body = $"{instance.Service} {instance.Version} started", Attributes = new Tags { { "service.version", instance.Version } },
                });
            }

            for (var i = rng.Poisson(seconds / 120.0); i > 0; i--)
            {
                output.Add(new SimLog
                {
                    Time = from + TimeSpan.FromSeconds(rng.Uniform(0, (to - from).TotalSeconds)),
                    Severity = SimSeverity.Info,
                    Body = $"Health check ready completed after {rng.Uniform(0.2, 3.5):0.0000}ms with status Healthy",
                    Instance = instance,
                    Category = "Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService",
                });
            }

            for (var i = rng.Poisson(seconds / 3600.0); i > 0; i--)
            {
                output.Add(new SimLog
                {
                    Time = from + TimeSpan.FromSeconds(rng.Uniform(0, (to - from).TotalSeconds)),
                    Severity = SimSeverity.Info,
                    Body = "Configuration reloaded from /app/appsettings.json",
                    Instance = instance,
                    Category = "Microsoft.Extensions.Configuration.ConfigurationManager",
                });
            }
        }
    }

    private List<SimSample> TakeSamples(DateTimeOffset at)
    {
        var elapsed = Math.Max(1.0, (at - (_lastSample ?? at.AddSeconds(-_options.LiveMetricIntervalSeconds))).TotalSeconds);
        var rng = new SimRandom(SimRandom.Hash(_options.Seed, _tenant.Name, "samples", at.ToUnixTimeSeconds()));
        var samples = new List<SimSample>();
        foreach (var instance in _env.Instances.ActiveAt(at))
        {
            var rps = _requestCounts.GetValueOrDefault(instance.PodName) / elapsed;
            var effect = _env.Incidents.Effect(instance.Service, null, at);
            var cpu = Math.Clamp(0.04 + rps * CpuPerRequest[instance.Service] * Math.Sqrt(effect.LatencyMultiplier) + rng.Normal() * 0.012, 0.01, 0.97);
            var phase = SimRandom.Hash(instance.PodName) % 1000 / 1000.0 * 2 * Math.PI;
            var sawtooth = 38 * Math.Abs(Math.Sin(2 * Math.PI * at.ToUnixTimeSeconds() / 420.0 + phase));
            var memoryMb = BaseMemoryMb[instance.Service] + rps * 0.8 + sawtooth + rng.Normal() * 2;

            samples.Add(Sample(instance, Instruments.ProcessCpu, cpu, at));
            samples.Add(Sample(instance, Instruments.ProcessMemory, Math.Round(memoryMb * 1024 * 1024), at));
            if (Services.UsingPostgres.Contains(instance.Service))
            {
                var used = Math.Clamp(Math.Round(rps * 0.9 + rng.Uniform(0, 1.5)), 0, 20);
                samples.Add(Sample(instance, Instruments.DbConnections, used, at, "used"));
                samples.Add(Sample(instance, Instruments.DbConnections, Math.Max(0, 6 - used), at, "idle"));
            }
        }
        _requestCounts.Clear();
        _lastSample = at;
        return samples;
    }

    private static SimSample Sample(ServiceInstance instance, string instrument, double value, DateTimeOffset at, string? state = null)
    {
        var tags = new Tags();
        if (state is not null)
        {
            tags.Add("db.client.connection.pool.name", "shop-db");
            tags.Add("db.client.connection.state", state);
        }
        return new SimSample { Instance = instance, Instrument = instrument, Value = value, Time = at, Attributes = tags };
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
