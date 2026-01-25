using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Keryhe.Telemetry.Client.Components.Shared;

public partial class MetricChart : ComponentBase
{
    [Parameter]
    public MetricSeries? Data { get; set; }

    [Parameter]
    public int Height { get; set; } = 400;

    [Parameter]
    public string? Title { get; set; }

    private List<ChartSeries> _chartSeries = new();
    private string[] _xAxisLabels = Array.Empty<string>();
    private ChartOptions _chartOptions = new();
    private bool _canShowChart = false;

    protected override void OnParametersSet()
    {
        if (Data == null || Data.Points == null || !Data.Points.Any())
        {
            _canShowChart = false;
            return;
        }

        // Only show chart for GAUGE and SUM types
        _canShowChart = Data.Type == MetricType.GAUGE || Data.Type == MetricType.SUM;

        if (_canShowChart)
        {
            BuildChartData();
        }
    }

    private void BuildChartData()
    {
        if (Data?.Points == null || !Data.Points.Any())
            return;

        // Format X-axis labels (timestamps)
        _xAxisLabels = Data.Points
            .Select(p => FormatTimestamp(p.Timestamp))
            .ToArray();

        // Build chart series
        var values = Data.Points
            .Select(p => (double)(p.DoubleValue ?? p.IntValue ?? 0))
            .ToArray();

        _chartSeries = new List<ChartSeries>
        {
            new ChartSeries
            {
                Name = Title ?? Data.Name,
                Data = values
            }
        };

        // Configure chart options
        var maxValue = values.Any() ? values.Max() : 0;
        var minValue = values.Any() ? values.Min() : 0;
        var range = maxValue - minValue;

        _chartOptions = new ChartOptions
        {
            YAxisTicks = range > 0 ? 5 : 1,
            ChartPalette = new[] { Colors.Blue.Default },
            LineStrokeWidth = 2,
            InterpolationOption = InterpolationOption.Straight
        };
    }

    private string FormatTimestamp(DateTime timestamp)
    {
        // Format based on data point density
        if (Data?.Points == null || !Data.Points.Any())
            return timestamp.ToString("HH:mm");

        var timeSpan = Data.Points.Max(p => p.Timestamp) - Data.Points.Min(p => p.Timestamp);

        // Less than 1 hour - show minutes and seconds
        if (timeSpan.TotalHours < 1)
            return timestamp.ToString("HH:mm:ss");

        // Less than 24 hours - show hours and minutes
        if (timeSpan.TotalHours < 24)
            return timestamp.ToString("HH:mm");

        // More than 24 hours - show date and time
        return timestamp.ToString("MM/dd HH:mm");
    }
}
