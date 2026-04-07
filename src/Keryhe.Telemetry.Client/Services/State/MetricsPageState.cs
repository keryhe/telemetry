using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Client.Services.State;

public class MetricsPageState
{
    public string SearchText { get; set; } = "";
    public string? SelectedService { get; set; }
    public MetricType? SelectedType { get; set; }
    public bool ShowUniqueNamesView { get; set; } = true;
}
