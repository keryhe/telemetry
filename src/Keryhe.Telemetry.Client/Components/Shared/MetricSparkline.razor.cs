using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Keryhe.Telemetry.Client.Components.Shared;

public partial class MetricSparkline : ComponentBase
{
    [Parameter]
    public List<MetricDataPoint>? DataPoints { get; set; }

    [Parameter]
    public int Height { get; set; } = 60;

    [Parameter]
    public Color Color { get; set; } = Color.Primary;

    private List<ChartSeries> _chartSeries = new();
    private string[] _xAxisLabels = Array.Empty<string>();
    private ChartOptions _chartOptions = new();

    protected override void OnParametersSet()
    {
        if (DataPoints == null || !DataPoints.Any())
        {
            _chartSeries = new List<ChartSeries>();
            return;
        }

        // Take last 20 data points for sparkline
        var points = DataPoints.TakeLast(20).ToList();

        // Build simple data array
        var values = points
            .Select(p => (double)(p.DoubleValue ?? p.IntValue ?? 0))
            .ToArray();

        _xAxisLabels = points.Select(p => "").ToArray(); // Empty labels for sparkline

        _chartSeries = new List<ChartSeries>
        {
            new ChartSeries
            {
                Name = "",
                Data = values
            }
        };

        // Minimal chart options for sparkline
        _chartOptions = new ChartOptions
        {
            ChartPalette = new[] { GetColorValue(Color) },
            YAxisTicks = 0,
            LineStrokeWidth = 1.5,
            InterpolationOption = InterpolationOption.Straight
        };
    }

    private string GetColorValue(Color color) => color switch
    {
        Color.Primary => Colors.Blue.Default,
        Color.Secondary => Colors.Gray.Default,
        Color.Success => Colors.Green.Default,
        Color.Info => Colors.LightBlue.Default,
        Color.Warning => Colors.Orange.Default,
        Color.Error => Colors.Red.Default,
        Color.Dark => Colors.Gray.Darken3,
        Color.Tertiary => Colors.Purple.Default,
        _ => Colors.Blue.Default
    };
}
