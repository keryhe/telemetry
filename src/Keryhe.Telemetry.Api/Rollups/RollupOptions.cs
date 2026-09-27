namespace Keryhe.Telemetry.Api.Rollups;

/// <summary>
/// Options for the periodic <see cref="RollupWorker"/>. Bound from the <c>Telemetry:Rollups</c>
/// configuration section (list-pages-server-side plan, Phase 2). Mirrors <c>RetentionOptions</c>'s shape.
/// </summary>
public sealed class RollupOptions
{
    /// <summary>Configuration section name these options bind from.</summary>
    public const string SectionName = "Telemetry:Rollups";

    /// <summary>Seconds between rollup cycles. Defaults to 30.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>
    /// A minute is rolled once it is this many seconds old (decision 38's "2 minutes after it
    /// closes"). Defaults to 120.
    /// </summary>
    public int SettleSeconds { get; set; } = 120;

    /// <summary>A minute is re-rolled once it reaches this age, to catch late arrivals. Defaults to 15.</summary>
    public int RepassMinutes { get; set; } = 15;

    /// <summary>Hours of history backfilled on first start, not the whole retention period. Defaults to 24.</summary>
    public int BackfillHours { get; set; } = 24;

    /// <summary>Lease duration for the per-granularity claim. Defaults to 120.</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>When false, the worker registers but never runs a cycle.</summary>
    public bool Enabled { get; set; } = true;
}
