namespace Keryhe.Telemetry.TestDataGenerator.Config;

/// <summary>Bound from the <c>Generator</c> configuration section.</summary>
public sealed class GeneratorOptions
{
    public const string SectionName = "Generator";

    /// <summary>
    /// The collector's OTLP endpoint: for <c>grpc</c> its gRPC address (h2c on 5117 in Development), for <c>http/protobuf</c> its base address
    /// (<c>http://localhost:5118</c> in Development; the SDK is given <c>/v1/traces</c>, <c>/v1/logs</c> and <c>/v1/metrics</c> under it).
    /// </summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:5117";

    /// <summary>The transport live data uses: <c>grpc</c> (default) or <c>http/protobuf</c>. Backfill always uses gRPC.</summary>
    public string Protocol { get; set; } = "grpc";

    /// <summary>Seeds every random decision, so the same seed yields the same telemetry.</summary>
    public int Seed { get; set; } = 42;

    /// <summary>Top-level user requests per second at the daily peak for a tenant of scale 1.0.</summary>
    public double PeakRequestsPerSecond { get; set; } = 2;

    /// <summary>Value of <c>deployment.environment.name</c>.</summary>
    public string Environment { get; set; } = "production";

    public int PodsPerService { get; set; } = 2;

    /// <summary>Metric export interval for live data (the SDK's periodic reader).</summary>
    public int LiveMetricIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// How far live emission trails the wall clock, so a request that started in the past second has
    /// finished (and its spans have a real end time) by the time it is replayed.
    /// </summary>
    public int LiveLagSeconds { get; set; } = 10;

    public BackfillOptions Backfill { get; set; } = new();

    /// <summary>Continue emitting in real time after the backfill (or instead of it).</summary>
    public bool Live { get; set; } = true;

    public List<TenantOptions> Tenants { get; set; } = [];

    public List<IncidentOptions> Incidents { get; set; } = [];

    public void Validate()
    {
        if (Tenants.Count == 0)
            throw new InvalidOperationException($"{SectionName}:Tenants is empty; configure at least one tenant.");
        foreach (var (t, i) in Tenants.Select((t, i) => (t, i)))
        {
            if (string.IsNullOrWhiteSpace(t.Name))
                throw new InvalidOperationException($"{SectionName}:Tenants:{i}:Name is not set.");
            if (string.IsNullOrWhiteSpace(t.ApiKey))
                throw new InvalidOperationException(
                    $"{SectionName}:Tenants:{i}:ApiKey is not set for tenant '{t.Name}'. Put it in user secrets: " +
                    $"dotnet user-secrets set \"{SectionName}:Tenants:{i}:ApiKey\" \"<key>\" --project src/Keryhe.Telemetry.TestDataGenerator");
            if (t.Scale <= 0)
                throw new InvalidOperationException($"{SectionName}:Tenants:{i}:Scale must be positive.");
        }
        if (!Protocol.Equals("grpc", StringComparison.OrdinalIgnoreCase) && !Protocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{SectionName}:Protocol must be 'grpc' or 'http/protobuf' (was '{Protocol}').");
        if (Backfill.Enabled && Protocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{SectionName}:Backfill sends hand-built OTLP over gRPC and shares {SectionName}:OtlpEndpoint with live data, so it cannot be combined with Protocol 'http/protobuf'. Backfill first over gRPC, then run live over HTTP.");
        if (PodsPerService < 1) throw new InvalidOperationException($"{SectionName}:PodsPerService must be at least 1.");
        if (PeakRequestsPerSecond <= 0) throw new InvalidOperationException($"{SectionName}:PeakRequestsPerSecond must be positive.");
    }
}

public sealed class BackfillOptions
{
    /// <summary>Off by default: backfilling is deliberate (a repeat over the same window stores it twice).</summary>
    public bool Enabled { get; set; }

    /// <summary>How much history to generate before going live.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Virtual seconds per backfill step, which is also the cumulative-metric export interval.</summary>
    public int ChunkSeconds { get; set; } = 60;

    /// <summary>Spans per Export call.</summary>
    public int MaxSpansPerExport { get; set; } = 2000;
}

public sealed class TenantOptions
{
    public string Name { get; set; } = "";

    /// <summary>The tenant's API key. Supply through user secrets or the environment, never appsettings.json.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Traffic multiplier relative to <see cref="GeneratorOptions.PeakRequestsPerSecond"/>.</summary>
    public double Scale { get; set; } = 1.0;
}

/// <summary>
/// A recurring daily incident, expressed in UTC time of day, so a 24-hour backfill always contains each
/// one exactly once. <see cref="Tenant"/> null applies it to every tenant.
/// </summary>
public sealed class IncidentOptions
{
    public string Name { get; set; } = "";
    public string? Tenant { get; set; }
    public string Service { get; set; } = "";

    /// <summary>Restrict to one server route (e.g. "POST /payments"); null affects the whole service.</summary>
    public string? Route { get; set; }

    public TimeSpan At { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(20);
    public double LatencyMultiplier { get; set; } = 1.0;

    /// <summary>Extra probability (0..1) that a request to the affected service fails.</summary>
    public double ExtraErrorRate { get; set; }

    /// <summary>When set, the service runs this version during the window (a bad deploy, then a rollback).</summary>
    public string? Version { get; set; }
}
