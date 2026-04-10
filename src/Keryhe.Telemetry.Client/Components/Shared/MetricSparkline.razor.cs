using ApexCharts;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazorColor = MudBlazor.Color;

namespace Keryhe.Telemetry.Client.Components.Shared;

public partial class MetricSparkline : ComponentBase
{
    [Parameter]
    public List<MetricDataPoint>? DataPoints { get; set; }

    [Parameter]
    public int Height { get; set; } = 60;

    [Parameter]
    public MudBlazorColor Color { get; set; } = MudBlazorColor.Primary;

    private record SparkPoint(int Index, double Value);

    private List<SparkPoint> _sparkData = new();
    private ApexChartOptions<SparkPoint> _chartOptions = new();

    protected override void OnParametersSet()
    {
        if (DataPoints == null || !DataPoints.Any())
        {
            _sparkData = new List<SparkPoint>();
            return;
        }

        var points = DataPoints.TakeLast(20).ToList();

        _sparkData = points
            .Select((p, i) => new SparkPoint(i, (double)(p.DoubleValue ?? p.IntValue ?? 0)))
            .ToList();

        var hexColor = Color switch
        {
            MudBlazorColor.Primary   => "#2196F3",
            MudBlazorColor.Secondary => "#9E9E9E",
            MudBlazorColor.Success   => "#4CAF50",
            MudBlazorColor.Info      => "#03A9F4",
            MudBlazorColor.Warning   => "#FF9800",
            MudBlazorColor.Error     => "#F44336",
            MudBlazorColor.Dark      => "#424242",
            MudBlazorColor.Tertiary  => "#9C27B0",
            _                        => "#2196F3"
        };

        _chartOptions = new ApexChartOptions<SparkPoint>
        {
            Chart = new Chart { Toolbar = new Toolbar { Show = false } },
            Colors = new List<string> { hexColor },
            Stroke = new Stroke { Curve = Curve.Straight, Width = new List<int> { 2 } },
            Xaxis = new XAxis { Labels = new XAxisLabels { Show = false }, AxisTicks = new AxisTicks { Show = false } },
            Yaxis = new List<YAxis> { new YAxis { Show = false } },
            Grid = new Grid { Show = false },
            Legend = new Legend { Show = false },
        };
    }
}
