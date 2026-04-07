namespace Keryhe.Telemetry.Client.Services.State;

public class ServiceMetricsPageState
{
    public string? SelectedService { get; set; }
    public bool AutoRefresh { get; set; } = false;
}
