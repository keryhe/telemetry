namespace Keryhe.Telemetry.Alerting;

/// <summary>
/// Options for the periodic <see cref="AlertEvaluationWorker"/>. Bound from the
/// <c>AlertEvaluation</c> configuration section.
/// </summary>
public sealed class AlertingOptions
{
    /// <summary>Configuration section name these options bind from.</summary>
    public const string SectionName = "AlertEvaluation";

    /// <summary>Seconds between alert-evaluation cycles. Defaults to 60.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>When false, the worker registers but never runs an evaluation cycle.</summary>
    public bool Enabled { get; set; } = true;
}
