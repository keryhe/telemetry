namespace Keryhe.Telemetry.Alerting.Models;

public class AlertResult
{
    public bool IsFiring { get; init; }
    public string Details { get; init; } = "";

    public static AlertResult NotFiring() => new() { IsFiring = false };
    public static AlertResult Firing(string details) => new() { IsFiring = true, Details = details };
}
