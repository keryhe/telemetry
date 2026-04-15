namespace Keryhe.Telemetry.Data.Access.Models;

public class AlertEventEntity
{
    public long Id { get; set; }
    public int RuleId { get; set; }
    public DateTime FiredAt { get; set; }
    public string DetailsJson { get; set; } = null!;

    public virtual AlertRuleEntity Rule { get; set; } = null!;
}
