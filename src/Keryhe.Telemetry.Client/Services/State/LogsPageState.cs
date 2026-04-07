namespace Keryhe.Telemetry.Client.Services.State;

public class LogsPageState
{
    public string SearchText { get; set; } = "";
    public string? SelectedService { get; set; }
    public string? SelectedSeverity { get; set; }
}
