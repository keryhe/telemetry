using System.Text.Json;
using Keryhe.Telemetry.StressTests.Browser;
using Keryhe.Telemetry.StressTests.Load;

namespace Keryhe.Telemetry.StressTests.Scenarios;

/// <summary>Conditions that stop a ramp (stress-test plan, Phase 6). They are stop rules that find the breaking point, not a pass/fail verdict (decision 6).</summary>
public sealed class RampCriteria
{
    /// <summary>Stop when more than this many records were dropped in a step.</summary>
    public long MaxDroppedRecords { get; set; }

    /// <summary>Stop when the step's gate-wait p95 (the worst signal) exceeds this: the gate is saturated and clients are being held.</summary>
    public double MaxGateWaitP95Ms { get; set; } = 250;

    /// <summary>Stop when any signal's client-side Export p99 for the step exceeds this.</summary>
    public double MaxExportP99Seconds { get; set; } = 5;

    /// <summary>Stop when more than this percentage of a signal's exports failed with a gRPC error.</summary>
    public double MaxErrorRatePercent { get; set; } = 1;

    /// <summary>Stop when ingest-to-queryable lag keeps growing across the step (the last third of its probes averaged this many times the first third's) rather than plateauing.</summary>
    public double LagGrowthFactor { get; set; } = 2;

    /// <summary>...and by at least this many milliseconds, so a lag that doubles from 50 ms to 100 ms is not growth. A probe that never became visible always counts.</summary>
    public double LagGrowthMinMs { get; set; } = 3000;
}

/// <summary>
/// The rate starts at <see cref="StartScale"/> times the profile's load rates and rises by <see cref="StepScale"/> every
/// <see cref="StepSeconds"/>, until a stop criterion holds for a whole step or <see cref="MaxSteps"/> is reached.
/// </summary>
public sealed class RampProfile
{
    public double StartScale { get; set; } = 1;
    public double StepScale { get; set; } = 1;
    public int StepSeconds { get; set; } = 60;
    public int MaxSteps { get; set; } = 30;
    public RampCriteria Criteria { get; set; } = new();
}

public sealed class BrowserProfile
{
    /// <summary>Concurrent users (decision 15: 1 to 5). 0 runs no browsers.</summary>
    public int Users { get; set; } = 1;
    public double ThinkTimeSeconds { get; set; } = 1;
    public bool Export { get; set; }
    public List<string> Windows { get; set; } = ["1h", "6h", "24h", "7d"];
    public double ReadyTimeoutSeconds { get; set; } = 60;

    public TourOptions ToTourOptions() => new()
    {
        Users = Users,
        ThinkTime = TimeSpan.FromSeconds(ThinkTimeSeconds),
        Export = Export,
        Windows = Windows,
        ReadyTimeout = TimeSpan.FromSeconds(ReadyTimeoutSeconds),
    };
}

/// <summary>
/// One stress profile (decisions 10, 17, 19): volumes, tenants, browsers, container limits and the worker timings that make
/// a retention sweep land inside the measured window. Every value has a default, so a profile JSON only lists what it overrides.
/// A profile with a <see cref="Ramp"/> section is a ramp scenario; without one it is a fixed-volume scenario.
/// </summary>
public sealed class ScenarioProfile
{
    public string Name { get; set; } = "custom";
    public int Tenants { get; set; } = 2;

    /// <summary>Ingestion only, before any browser starts, so rollup coverage and data volume exist. Excluded from the headline numbers.</summary>
    public int WarmupSeconds { get; set; } = 60;
    public int MeasuredSeconds { get; set; } = 300;

    public LoadProfile Load { get; set; } = new();
    public BrowserProfile Browsers { get; set; } = new();

    /// <summary>Decision 17: the same CPU/memory cap for every DB container.</summary>
    public double ContainerCpus { get; set; } = 4;
    public double ContainerMemoryGb { get; set; } = 8;

    /// <summary>Short, so at least one retention sweep lands in the measured window (decision 11).</summary>
    public int RetentionIntervalSeconds { get; set; } = 30;

    public int MarkerIntervalSeconds { get; set; } = 5;
    public int QuiesceStableSeconds { get; set; } = 10;
    public int QuiesceTimeoutSeconds { get; set; } = 180;

    public RampProfile? Ramp { get; set; }

    public bool IsRamp => Ramp is not null;

    public static IReadOnlyList<string> Builtin { get; } = ["smoke", "standard", "soak", "ramp"];

    /// <summary>Loads a built-in profile by name (from the <c>Profiles/</c> folder beside the executable) or any profile JSON by path.</summary>
    public static ScenarioProfile Resolve(string nameOrPath)
    {
        var path = File.Exists(nameOrPath) ? nameOrPath : Path.Combine(AppContext.BaseDirectory, "Profiles", nameOrPath + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Profile '{nameOrPath}' not found (built-ins: {string.Join(", ", Builtin)}; or pass a JSON path).", path);
        var profile = JsonSerializer.Deserialize<ScenarioProfile>(File.ReadAllText(path), LoadProfile.JsonOptions)
                      ?? throw new InvalidDataException($"'{path}' is empty.");
        if (profile.Name == "custom") profile.Name = Path.GetFileNameWithoutExtension(path);
        profile.Validate();
        return profile;
    }

    public void Validate()
    {
        if (Tenants < 1) throw new InvalidDataException($"Profile '{Name}': Tenants must be at least 1.");
        if (MeasuredSeconds < 1 && Ramp is null) throw new InvalidDataException($"Profile '{Name}': MeasuredSeconds must be positive.");
        if (Browsers.Users is < 0 or > 5) throw new InvalidDataException($"Profile '{Name}': Browsers.Users must be 0 to 5.");
        if (Ramp is { StepSeconds: < 1 } or { MaxSteps: < 1 } or { StepScale: <= 0 } or { StartScale: <= 0 })
            throw new InvalidDataException($"Profile '{Name}': Ramp needs positive StartScale, StepScale, StepSeconds and MaxSteps.");
    }
}
