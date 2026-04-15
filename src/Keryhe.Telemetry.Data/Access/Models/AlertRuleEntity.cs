namespace Keryhe.Telemetry.Data.Access.Models;

public class AlertRuleEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Type { get; set; } = null!;
    public string? ServiceName { get; set; }
    public string ConditionJson { get; set; } = null!;
    public string WebhookUrl { get; set; } = null!;
    public int CooldownMinutes { get; set; }
    public bool Enabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastFiredAt { get; set; }

    public virtual ICollection<AlertEventEntity> Events { get; set; } = new List<AlertEventEntity>();
}
