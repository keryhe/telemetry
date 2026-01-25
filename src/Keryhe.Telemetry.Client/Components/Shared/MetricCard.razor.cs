using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Keryhe.Telemetry.Client.Components.Shared;

public partial class MetricCard : ComponentBase
{
    [Parameter, EditorRequired]
    public string MetricName { get; set; } = "";

    [Parameter]
    public string? Unit { get; set; }

    [Parameter]
    public MetricType MetricType { get; set; }

    [Parameter]
    public double? CurrentValue { get; set; }

    [Parameter]
    public double? MinValue { get; set; }

    [Parameter]
    public double? MaxValue { get; set; }

    [Parameter]
    public double? AvgValue { get; set; }

    [Parameter]
    public double? PreviousValue { get; set; }

    [Parameter]
    public List<MetricDataPoint>? DataPoints { get; set; }

    [Parameter]
    public bool ShowStats { get; set; } = true;

    [Parameter]
    public EventCallback<string> OnClick { get; set; }

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    private int? TrendDirection
    {
        get
        {
            if (!CurrentValue.HasValue || !PreviousValue.HasValue)
                return null;

            var diff = CurrentValue.Value - PreviousValue.Value;
            if (Math.Abs(diff) < 0.01) // Threshold for "no change"
                return 0;

            return diff > 0 ? 1 : -1;
        }
    }

    private Color TypeColor => MetricType switch
    {
        MetricType.GAUGE => Color.Primary,
        MetricType.SUM => Color.Success,
        MetricType.HISTOGRAM => Color.Warning,
        MetricType.EXPONENTIAL_HISTOGRAM => Color.Warning,
        MetricType.SUMMARY => Color.Info,
        _ => Color.Default
    };

    private string GetTrendIcon() => TrendDirection switch
    {
        1 => Icons.Material.Filled.TrendingUp,
        -1 => Icons.Material.Filled.TrendingDown,
        0 => Icons.Material.Filled.TrendingFlat,
        _ => Icons.Material.Filled.Remove
    };

    private Color GetTrendColor() => TrendDirection switch
    {
        1 => Color.Success,
        -1 => Color.Error,
        0 => Color.Secondary,
        _ => Color.Default
    };

    private string FormatValue(double? value)
    {
        if (!value.HasValue)
            return "N/A";

        // Format based on magnitude
        if (Math.Abs(value.Value) >= 1_000_000_000)
            return $"{value.Value / 1_000_000_000:F1}B";
        if (Math.Abs(value.Value) >= 1_000_000)
            return $"{value.Value / 1_000_000:F1}M";
        if (Math.Abs(value.Value) >= 1_000)
            return $"{value.Value / 1_000:F1}K";

        return $"{value.Value:F2}";
    }

    private async Task NavigateToDetail()
    {
        if (OnClick.HasDelegate)
        {
            await OnClick.InvokeAsync(MetricName);
        }
        else
        {
            NavigationManager.NavigateTo($"/metrics/detail/{Uri.EscapeDataString(MetricName)}");
        }
    }
}
