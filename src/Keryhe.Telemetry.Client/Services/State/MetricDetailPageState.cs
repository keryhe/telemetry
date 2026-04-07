namespace Keryhe.Telemetry.Client.Services.State;

public class MetricDetailPageState
{
    public string? CurrentMetricName { get; set; }
    public bool AutoRefresh { get; set; } = false;
    public string? SelectedServiceFilter { get; set; }
    public Dictionary<string, string> SelectedLabelFilters { get; set; } = new();
    public bool ShowPerServiceView { get; set; } = false;
    public int ActiveTab { get; set; } = 0;
}
