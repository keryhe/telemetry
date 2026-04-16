using ApexCharts;
using Keryhe.Telemetry.Client.Services.State;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;

namespace Keryhe.Telemetry.Client.Components.Shared;

public partial class MetricChart : ComponentBase
{
    [Inject]
    private TimeRangeState TimeRangeState { get; set; } = null!;

    [Parameter]
    public MetricSeries? Data { get; set; }

    [Parameter]
    public MultiSeriesMetricData? MultiSeriesData { get; set; }

    [Parameter]
    public int Height { get; set; } = 400;

    [Parameter]
    public string? Title { get; set; }

    private record DataPoint(DateTime Time, double Value);

    private List<(string Name, List<DataPoint> Points)> _apexSeries = new();
    private ApexChartOptions<DataPoint> _chartOptions = new();
    private bool _canShowChart = false;

    protected override void OnParametersSet()
    {
        _apexSeries = new();
        _canShowChart = false;

        if (MultiSeriesData != null && MultiSeriesData.Series.Count > 1
            && (MultiSeriesData.Type == MetricType.GAUGE || MultiSeriesData.Type == MetricType.SUM))
        {
            _canShowChart = true;
            BuildMultiSeriesChartData();
            return;
        }

        if (Data == null || Data.Points == null || !Data.Points.Any())
            return;

        switch (Data.Type)
        {
            case MetricType.GAUGE:
            case MetricType.SUM:
                _canShowChart = true;
                BuildSingleValueChartData();
                break;
            case MetricType.HISTOGRAM:
            case MetricType.EXPONENTIAL_HISTOGRAM:
                _canShowChart = true;
                BuildHistogramTrendChartData();
                break;
            case MetricType.SUMMARY:
                _canShowChart = true;
                BuildSummaryQuantileChartData();
                break;
        }
    }

    private void BuildSingleValueChartData()
    {
        if (Data?.Points == null || !Data.Points.Any()) return;

        var points = Data.Points
            .Select(p => new DataPoint(p.Timestamp, (double)(p.DoubleValue ?? p.IntValue ?? 0)))
            .ToList();

        _apexSeries = new List<(string, List<DataPoint>)>
        {
            (Title ?? Data.Name, points)
        };

        _chartOptions = BuildLineOptions(new[] { "#2196F3" }, showMarkers: true);
    }

    private void BuildHistogramTrendChartData()
    {
        if (Data?.Points == null || !Data.Points.Any()) return;

        _apexSeries = new List<(string, List<DataPoint>)>();

        _apexSeries.Add(("Count", Data.Points
            .Select(p => new DataPoint(p.Timestamp, (double)(p.Count ?? 0)))
            .ToList()));

        if (Data.Points.Any(p => p.Sum.HasValue))
            _apexSeries.Add(("Sum", Data.Points
                .Select(p => new DataPoint(p.Timestamp, p.Sum ?? double.NaN))
                .ToList()));

        if (Data.Points.Any(p => p.Min.HasValue))
            _apexSeries.Add(("Min", Data.Points
                .Select(p => new DataPoint(p.Timestamp, p.Min ?? double.NaN))
                .ToList()));

        if (Data.Points.Any(p => p.Max.HasValue))
            _apexSeries.Add(("Max", Data.Points
                .Select(p => new DataPoint(p.Timestamp, p.Max ?? double.NaN))
                .ToList()));

        _chartOptions = BuildLineOptions(new[] { "#2196F3", "#4CAF50", "#FF9800", "#F44336" }, showMarkers: true);
    }

    private void BuildSummaryQuantileChartData()
    {
        if (Data?.Points == null || !Data.Points.Any()) return;

        var quantiles = Data.Points
            .Where(p => p.Quantiles != null)
            .SelectMany(p => p.Quantiles!)
            .Distinct()
            .OrderBy(q => q)
            .ToList();

        if (!quantiles.Any()) { _canShowChart = false; return; }

        _apexSeries = new List<(string, List<DataPoint>)>();

        foreach (var quantile in quantiles)
        {
            var pts = new List<DataPoint>();
            for (int i = 0; i < Data.Points.Count; i++)
            {
                var point = Data.Points[i];
                double value = double.NaN;
                if (point.Quantiles != null && point.QuantileValues != null)
                {
                    for (int j = 0; j < point.Quantiles.Count && j < point.QuantileValues.Count; j++)
                    {
                        if (Math.Abs(point.Quantiles[j] - quantile) < 0.0000001)
                        {
                            value = point.QuantileValues[j];
                            break;
                        }
                    }
                }
                pts.Add(new DataPoint(point.Timestamp, value));
            }
            _apexSeries.Add(($"P{quantile * 100:F0}", pts));
        }

        _chartOptions = BuildLineOptions(
            new[] { "#2196F3", "#4CAF50", "#F44336", "#FF9800", "#9C27B0", "#009688" },
            showMarkers: true);
    }

    private void BuildMultiSeriesChartData()
    {
        if (MultiSeriesData?.Series == null || !MultiSeriesData.Series.Any()) return;

        _apexSeries = MultiSeriesData.Series.Select(series =>
        {
            var pts = series.Points
                .Select(p => new DataPoint(p.Timestamp, (double)(p.DoubleValue ?? p.IntValue ?? 0)))
                .ToList();
            return (series.SeriesName, pts);
        }).ToList();

        _chartOptions = BuildLineOptions(
            new[] { "#2196F3", "#4CAF50", "#F44336", "#FF9800", "#9C27B0", "#009688",
                    "#E91E63", "#FFC107", "#00BCD4", "#673AB7" },
            showMarkers: true);
    }

    private Task HandleChartZoomed(ZoomedData<DataPoint> e)
    {
        if (e.XAxis?.Min == null || e.XAxis?.Max == null) return Task.CompletedTask;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Min)).UtcDateTime;
        var end   = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Max)).UtcDateTime;
        TimeRangeState.SetCustomRange(start, end);
        return Task.CompletedTask;
    }

    private static ApexChartOptions<DataPoint> BuildLineOptions(string[] colors, bool showMarkers)
    {
        return new ApexChartOptions<DataPoint>
        {
            Chart = new Chart { Toolbar = new Toolbar { Show = false }, Zoom = new Zoom { Enabled = true, Type = AxisType.X, AllowMouseWheelZoom = false } },
            Colors = colors.ToList(),
            Stroke = new Stroke { Curve = Curve.Straight, Width = new List<int> { 2 } },
            Markers = new Markers { Size = showMarkers ? new List<int> { 4 } : new List<int> { 0 } },
            Xaxis = new XAxis { Type = XAxisType.Datetime },
        };
    }
}
