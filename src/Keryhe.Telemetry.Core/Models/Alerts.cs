namespace Keryhe.Telemetry.Core.Models;

// =============================================================================
// ALERT RULE MODELS
// =============================================================================

public enum AlertRuleType
{
    MetricThreshold,
    ErrorRate,
    SlowTrace,
    LogSeveritySpike
}

public class AlertRule
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public AlertRuleType Type { get; set; }
    public string? ServiceName { get; set; }
    public string ConditionJson { get; set; } = null!;
    public string WebhookUrl { get; set; } = null!;
    public int CooldownMinutes { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastFiredAt { get; set; }
}

public class AlertEvent
{
    public long Id { get; set; }
    public int RuleId { get; set; }
    public DateTime FiredAt { get; set; }
    public string DetailsJson { get; set; } = null!;
    public AlertRule? Rule { get; set; }
}
