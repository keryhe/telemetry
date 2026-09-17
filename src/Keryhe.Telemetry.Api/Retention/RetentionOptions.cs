namespace Keryhe.Telemetry.Api.Retention;

/// <summary>
/// Options for the periodic <see cref="RetentionWorker"/>. Bound from the <c>Retention</c>
/// configuration section. This is the sweep's operational cadence only — how many days of
/// telemetry to keep lives in the DB-backed <c>retention_settings</c> row
/// (<see cref="Keryhe.Telemetry.Core.IRetentionSettingsRepository"/>), not here.
/// </summary>
public sealed class RetentionOptions
{
    /// <summary>Configuration section name these options bind from.</summary>
    public const string SectionName = "Retention";

    /// <summary>Seconds between retention-sweep cycles. Defaults to 3600 (1 hour).</summary>
    public int IntervalSeconds { get; set; } = 3600;

    /// <summary>When false, the worker registers but never runs a sweep cycle.</summary>
    public bool Enabled { get; set; } = true;
}
