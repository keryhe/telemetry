using System.Net.Http.Json;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Alerting.Notifications;

public class WebhookNotificationChannel : INotificationChannel
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookNotificationChannel> _logger;

    public WebhookNotificationChannel(IHttpClientFactory httpClientFactory, ILogger<WebhookNotificationChannel> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task SendAsync(AlertRule rule, string details, DateTime firedAt, CancellationToken ct = default)
    {
        var payload = new WebhookPayload
        {
            AlertName = rule.Name,
            Type = rule.Type.ToString(),
            Service = rule.ServiceName,
            FiredAt = firedAt,
            Details = details,
            RuleId = rule.Id
        };

        var client = _httpClientFactory.CreateClient("AlertWebhook");

        try
        {
            var response = await client.PostAsJsonAsync(rule.WebhookUrl, payload, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Webhook POST for rule {RuleId} returned {StatusCode}.", rule.Id, response.StatusCode);
            else
                _logger.LogInformation("Alert '{AlertName}' fired — webhook delivered.", rule.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to deliver webhook for rule {RuleId} to {Url}.", rule.Id, rule.WebhookUrl);
        }
    }
}
