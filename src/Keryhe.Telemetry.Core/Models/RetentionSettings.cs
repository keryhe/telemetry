namespace Keryhe.Telemetry.Core.Models;

// =============================================================================
// RETENTION SETTINGS MODEL
// =============================================================================

/// <summary>
/// The single, global retention configuration row. Untenanted and singleton — see
/// <see cref="IRetentionSettingsRepository"/>.
/// </summary>
public class RetentionSettings
{
    public int TraceRetentionDays { get; set; }
    public int LogRetentionDays { get; set; }
    public int MetricRetentionDays { get; set; }
    public DateTime UpdatedAt { get; set; }
}
