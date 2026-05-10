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

    [Parameter]
    public bool ShowRaw { get; set; }

    [Parameter]
    public string? Unit { get; set; }

    private record DataPoint(DateTime Time, double Value);

    private List<(string Name, List<DataPoint> Points)> _apexSeries = new();
    private List<(string Name, List<DataPoint> Points)> _apexSeries2 = new();
    private ApexChartOptions<DataPoint> _chartOptions = new();
    private ApexChartOptions<DataPoint> _chartOptions2 = new();
    private bool _canShowChart = false;
    private SeriesType _seriesType = SeriesType.Line;

    private ApexChart<DataPoint>? _chart;
    private int _chartRenderKey = 0;
    private string[]? _lastColors;
    private bool? _lastShowMarkers;
    private MetricType? _lastBuiltMetricType;

    protected override void OnParametersSet()
    {
        _apexSeries = new();
        _apexSeries2 = new();

        if (MultiSeriesData != null && MultiSeriesData.Series.Count > 1
            && (MultiSeriesData.Type == MetricType.GAUGE || MultiSeriesData.Type == MetricType.SUM))
        {
            BuildMultiSeriesChartData();
            if (_seriesType == SeriesType.Bar)
            {
                var (msStart, msEnd) = TimeRangeState.GetDateTimeRange();
                _chartOptions = BuildBarOptions(ToApexMs(msStart), ToApexMs(msEnd), FormatUnit(Unit));
            }
            else
            {
                BuildLineOptionsIfNeeded(MultiSeriesData.Type);
            }
            _canShowChart = true;
            _chartRenderKey++;
            return;
        }

        if (Data == null || Data.Points == null || !Data.Points.Any())
        {
            _canShowChart = false;
            return;
        }

        switch (Data.Type)
        {
            case MetricType.GAUGE:
                _seriesType = SeriesType.Line;
                BuildSingleValueChartData();
                break;
            case MetricType.SUM:
                if (IsDelta(Data.Points))
                {
                    _seriesType = SeriesType.Bar;
                    BuildDeltaBarChartData();
                }
                else if (IsCounter(Data.Points) && !ShowRaw)
                {
                    _seriesType = SeriesType.Line;
                    BuildCounterRateChartData();
                }
                else
                {
                    _seriesType = SeriesType.Line;
                    BuildSingleValueChartData();
                }
                break;
            case MetricType.HISTOGRAM:
            case MetricType.EXPONENTIAL_HISTOGRAM:
                _seriesType = SeriesType.Line;
                BuildHistogramTrendChartData();
                break;
            case MetricType.SUMMARY:
                _seriesType = SeriesType.Line;
                BuildSummaryQuantileChartData();
                break;
            default:
                _canShowChart = false;
                return;
        }

        if (_seriesType == SeriesType.Bar)
        {
            var (start, end) = TimeRangeState.GetDateTimeRange();
            _chartOptions = BuildBarOptions(ToApexMs(start), ToApexMs(end), FormatUnit(Unit));
        }
        else
        {
            BuildLineOptionsIfNeeded(Data.Type);
        }
        _canShowChart = true;
        _chartRenderKey++;
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
    }

    private void BuildHistogramTrendChartData()
    {
        if (Data?.Points == null || !Data.Points.Any()) return;

        // Chart 1: Count + Sum (share the same magnitude scale)
        _apexSeries = new List<(string, List<DataPoint>)>();
        _apexSeries.Add(("Count", Data.Points
            .Select(p => new DataPoint(p.Timestamp, (double)(p.Count ?? 0)))
            .ToList()));

        if (Data.Points.Any(p => p.Sum.HasValue))
            _apexSeries.Add(("Sum", Data.Points
                .Select(p => new DataPoint(p.Timestamp, p.Sum ?? double.NaN))
                .ToList()));

        // Chart 2: Min + Max (often a very different scale from Count/Sum)
        _apexSeries2 = new List<(string, List<DataPoint>)>();

        if (Data.Points.Any(p => p.Min.HasValue))
            _apexSeries2.Add(("Min", Data.Points
                .Select(p => new DataPoint(p.Timestamp, p.Min ?? double.NaN))
                .ToList()));

        if (Data.Points.Any(p => p.Max.HasValue))
            _apexSeries2.Add(("Max", Data.Points
                .Select(p => new DataPoint(p.Timestamp, p.Max ?? double.NaN))
                .ToList()));
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
    }

    private void BuildMultiSeriesChartData()
    {
        if (MultiSeriesData?.Series == null || !MultiSeriesData.Series.Any()) return;

        var firstPoints = MultiSeriesData.Series.FirstOrDefault()?.Points;
        bool isCounterSeries = firstPoints != null && IsCounter(firstPoints);
        bool isDeltaSeries = firstPoints != null && IsDelta(firstPoints);

        _seriesType = isDeltaSeries ? SeriesType.Bar : SeriesType.Line;

        _apexSeries = MultiSeriesData.Series.Select(series =>
        {
            List<DataPoint> pts;
            if (isCounterSeries && !ShowRaw)
            {
                pts = ComputeRateSeries(series.Points);
            }
            else
            {
                pts = series.Points
                    .Select(p => new DataPoint(p.Timestamp, (double)(p.DoubleValue ?? p.IntValue ?? 0)))
                    .ToList();
            }
            return (series.SeriesName, pts);
        }).ToList();
    }

    private void BuildDeltaBarChartData()
    {
        if (Data?.Points == null || !Data.Points.Any()) return;

        var points = Data.Points
            .Select(p => new DataPoint(p.Timestamp, (double)(p.DoubleValue ?? p.IntValue ?? 0)))
            .ToList();

        _apexSeries = new List<(string, List<DataPoint>)>
        {
            (Title ?? Data.Name, points)
        };
    }

    private void BuildCounterRateChartData()
    {
        if (Data?.Points == null || !Data.Points.Any()) return;

        var ratePoints = ComputeRateSeries(Data.Points);
        _apexSeries = new List<(string, List<DataPoint>)>
        {
            (Title ?? Data.Name, ratePoints)
        };
    }

    private static bool IsCounter(List<MetricDataPoint> pts)
    {
        var first = pts.FirstOrDefault();
        return first?.IsMonotonic == true
            && first.AggregationTemporality == AggregationTemporality.CUMULATIVE;
    }

    private static bool IsDelta(List<MetricDataPoint> pts)
    {
        var first = pts.FirstOrDefault();
        return first?.AggregationTemporality == AggregationTemporality.DELTA;
    }

    private static List<DataPoint> ComputeRateSeries(List<MetricDataPoint> pts)
    {
        var result = new List<DataPoint>(Math.Max(0, pts.Count - 1));
        for (int i = 1; i < pts.Count; i++)
        {
            var prev = pts[i - 1];
            var curr = pts[i];
            double prevVal = (double)(prev.DoubleValue ?? prev.IntValue ?? 0);
            double currVal = (double)(curr.DoubleValue ?? curr.IntValue ?? 0);

            // Skip counter resets
            if (currVal < prevVal) continue;

            double deltaSeconds = (curr.Timestamp - prev.Timestamp).TotalSeconds;
            if (deltaSeconds <= 0) continue;

            result.Add(new DataPoint(curr.Timestamp, (currVal - prevVal) / deltaSeconds));
        }
        return result;
    }

    private static ApexChartOptions<DataPoint> BuildBarOptions(decimal xMin, decimal xMax, string? yAxisTitle = null)
    {
        var opts = new ApexChartOptions<DataPoint>
        {
            Chart = new Chart { Toolbar = new Toolbar { Show = false }, Zoom = new Zoom { Enabled = true, Type = AxisType.X, AllowMouseWheelZoom = false } },
            Colors = new List<string> { "#2196F3" },
            PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { ColumnWidth = "70%" } },
            Xaxis = new XAxis { Type = XAxisType.Datetime, Min = xMin, Max = xMax },
        };
        if (!string.IsNullOrEmpty(yAxisTitle))
            opts.Yaxis = new List<YAxis> { new() { Title = new AxisTitle { Text = yAxisTitle } } };
        return opts;
    }

    private void BuildLineOptionsIfNeeded(MetricType type)
    {
        if (_lastBuiltMetricType != type)
        {
            _lastBuiltMetricType = type;
            (_lastColors, _lastShowMarkers) = type switch
            {
                MetricType.HISTOGRAM or MetricType.EXPONENTIAL_HISTOGRAM =>
                    (new[] { "#2196F3", "#4CAF50", "#FF9800", "#F44336" }, true),
                MetricType.SUMMARY =>
                    (new[] { "#2196F3", "#4CAF50", "#F44336", "#FF9800", "#9C27B0", "#009688" }, true),
                _ when MultiSeriesData != null =>
                    (new[] { "#2196F3", "#4CAF50", "#F44336", "#FF9800", "#9C27B0", "#009688",
                             "#E91E63", "#FFC107", "#00BCD4", "#673AB7" }, true),
                _ => (new[] { "#2196F3" }, true),
            };
        }

        var (start, end) = TimeRangeState.GetDateTimeRange();
        _chartOptions = BuildLineOptions(_lastColors!, _lastShowMarkers!.Value, ToApexMs(start), ToApexMs(end), GetYAxisTitle());

        // Build second chart options for histogram Min/Max split
        if (type is MetricType.HISTOGRAM or MetricType.EXPONENTIAL_HISTOGRAM)
        {
            // Orange + Red for Min/Max
            _chartOptions2 = BuildLineOptions(new[] { "#FF9800", "#F44336" }, true, ToApexMs(start), ToApexMs(end), FormatUnit(Unit));
        }
    }

    private static decimal ToApexMs(DateTime utc)
        => (decimal)new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private Task HandleChartZoomed(ZoomedData<DataPoint> e)
    {
        if (e.XAxis?.Min == null || e.XAxis?.Max == null) return Task.CompletedTask;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Min)).UtcDateTime;
        var end   = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Max)).UtcDateTime;
        if (start >= end) return Task.CompletedTask;
        TimeRangeState.SetCustomRange(start, end);
        return Task.CompletedTask;
    }

    private static ApexChartOptions<DataPoint> BuildLineOptions(string[] colors, bool showMarkers, decimal xMin, decimal xMax, string? yAxisTitle = null)
    {
        var opts = new ApexChartOptions<DataPoint>
        {
            Chart = new Chart { Toolbar = new Toolbar { Show = false }, Zoom = new Zoom { Enabled = true, Type = AxisType.X, AllowMouseWheelZoom = false } },
            Colors = colors.ToList(),
            Stroke = new Stroke { Curve = Curve.Straight, Width = new List<int> { 2 } },
            Markers = new Markers { Size = showMarkers ? new List<int> { 4 } : new List<int> { 0 } },
            Xaxis = new XAxis { Type = XAxisType.Datetime, Min = xMin, Max = xMax },
        };
        if (!string.IsNullOrEmpty(yAxisTitle))
            opts.Yaxis = new List<YAxis> { new() { Title = new AxisTitle { Text = yAxisTitle } } };
        return opts;
    }

    private string? GetYAxisTitle()
    {
        var unit = FormatUnit(Unit);
        // Counter rate charts append /s to the unit
        if (Data?.Type == MetricType.SUM && Data.Points.Any() && IsCounter(Data.Points) && !ShowRaw)
            return string.IsNullOrEmpty(unit) ? "/s" : $"{unit}/s";
        // Multi-series counter rate
        if (MultiSeriesData?.Type == MetricType.SUM)
        {
            var firstPts = MultiSeriesData.Series.FirstOrDefault()?.Points;
            if (firstPts != null && IsCounter(firstPts) && !ShowRaw)
                return string.IsNullOrEmpty(unit) ? "/s" : $"{unit}/s";
        }
        return unit;
    }

    private static string? FormatUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return null;
        // Strip OTel curly-brace convention: {requests} -> requests
        if (unit.StartsWith('{') && unit.EndsWith('}') && unit.Length > 2)
            return unit[1..^1];
        return unit;
    }
}