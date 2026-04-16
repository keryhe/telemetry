using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Alerting.Notifications;

public interface INotificationChannel
{
    Task SendAsync(AlertRule rule, string details, DateTime firedAt, CancellationToken ct = default);
}
