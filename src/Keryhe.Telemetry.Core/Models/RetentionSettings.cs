namespace Keryhe.Telemetry.Core.Models;

// =============================================================================
// RETENTION SETTINGS MODEL
// =============================================================================

/// <summary>
/// One tenant's retention windows, in days (columns on the control plane's <c>tenants</c> row) --
/// see <see cref="IRetentionSettingsRepository"/>. There is no global default: a new tenant takes the
/// schema's column defaults, equal to <see cref="RetentionDefaults"/>.
/// </summary>
public class RetentionSettings
{
    public int TraceRetentionDays { get; set; }
    public int LogRetentionDays { get; set; }
    public int MetricRetentionDays { get; set; }
    public DateTime UpdatedAt { get; set; }
}
