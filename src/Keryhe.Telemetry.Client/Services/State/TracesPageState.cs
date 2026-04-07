namespace Keryhe.Telemetry.Client.Services.State;

public class TracesPageState
{
    public int ActiveTabIndex { get; set; } = 0;
    public string SearchText { get; set; } = "";
    public string? SelectedService { get; set; }
    public string FilterMode { get; set; } = "all";
    public int MinDurationMs { get; set; } = 500;
    public string? SelectedAnalyticsService { get; set; }
}
