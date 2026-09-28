namespace Keryhe.Telemetry.StressTests.Browser;

/// <summary>Knobs for the headless-browser tour (stress-test plan, Phase 5). Every value has a default.</summary>
public sealed class TourOptions
{
    /// <summary>Concurrent simulated users, each in its own browser context and pinned to one tenant round-robin (decision 15: 1 to 5).</summary>
    public int Users { get; set; } = 2;

    /// <summary>Pause between tour steps.</summary>
    public TimeSpan ThinkTime { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A step that is not ready by then is recorded as a timeout, with a screenshot.</summary>
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The tour rotates through these UI time-range presets, one per loop. The UI offers 1h/3h/6h/12h/24h/3d/7d; the long ones reach past rollup coverage and the standard tier's raw-search window.</summary>
    public List<string> Windows { get; set; } = ["1h", "6h", "24h", "7d"];

    /// <summary>Search text for the free-text steps. Must appear in the data the load generator sends.</summary>
    public string FreeText { get; set; } = "op";

    /// <summary>A <c>key:value</c> attribute search for the attribute-search steps.</summary>
    public string KeyValue { get; set; } = "attr.k0:v1";

    /// <summary>How long start-up waits for the metrics catalog to list a histogram and a sum metric (the catalog trails ingestion by the metric touch interval).</summary>
    public TimeSpan DiscoveryWait { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Decision 18: one export download per signal, off by default.</summary>
    public bool Export { get; set; }

    public bool Headed { get; set; }
}

/// <summary>One <c>/api</c> response a page made during a step.</summary>
public sealed record ApiRequestRecord(
    DateTimeOffset At, string Method, string Template, int Status, double DurationMs, long? ResponseBytes,
    string? Source, bool? TotalIsLowerBound, bool? TimedOut, bool Failed);

/// <summary>What one tour step measured. <see cref="ReadyMs"/> is null on a timeout or error.</summary>
public sealed record PageResult(
    int User, long TenantId, string Step, string Page, string Window, DateTimeOffset StartedAt,
    double? ReadyMs, bool TimedOut, string? Error, string? Screenshot,
    IReadOnlyList<ApiRequestRecord> Requests, IReadOnlyList<string> ConsoleErrors);

/// <summary><see cref="Discovery"/> is what each user's tenant offered the plan (a service and a histogram and a sum metric), so a missing step is explainable.</summary>
public sealed record TourResults(IReadOnlyList<PageResult> Pages, int Iterations, IReadOnlyList<TourData> Discovery);
