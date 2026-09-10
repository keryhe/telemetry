namespace Keryhe.Telemetry.Core.Alerting;

public interface IAlertService
{
    Task EvaluateAllAsync(CancellationToken ct = default);
}
