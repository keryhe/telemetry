namespace Keryhe.Telemetry.Alerting;

public interface IAlertService
{
    Task EvaluateAllAsync(CancellationToken ct = default);
}
