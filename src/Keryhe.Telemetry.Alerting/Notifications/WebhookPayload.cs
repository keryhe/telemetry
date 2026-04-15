using System.Text.Json.Serialization;

namespace Keryhe.Telemetry.Alerting.Notifications;

public class WebhookPayload
{
    [JsonPropertyName("alert_name")]
    public string AlertName { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("fired_at")]
    public DateTime FiredAt { get; set; }

    [JsonPropertyName("details")]
    public string Details { get; set; } = "";

    [JsonPropertyName("rule_id")]
    public int RuleId { get; set; }
}
