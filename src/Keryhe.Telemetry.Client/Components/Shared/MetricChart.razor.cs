using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Keryhe.Telemetry.Client.Components.Shared;

public partial class MetricChart : ComponentBase
{
    [Parameter]
    public MetricSeries? Data { get; set; }

    [Parameter]
    public MultiSeriesMetricData? MultiSeriesData { get; set; }

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
        _chartSeries = new List<ChartSeries>();
        _xAxisLabels = Array.Empty<string>();
        _canShowChart = false;

        // Multi-series mode
        if (MultiSeriesData != null && MultiSeriesData.Series.Count > 1
            && (MultiSeriesData.Type == MetricType.GAUGE || MultiSeriesData.Type == MetricType.SUM))
        {
            _canShowChart = true;
            BuildMultiSeriesChartData();
            return;
        }

        if (Data == null || Data.Points == null || !Data.Points.Any())
        {
            return;
        }

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
                Data = values,
                ShowDataMarkers = true
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

    private void BuildHistogramTrendChartData()
    {
        if (Data?.Points == null || !Data.Points.Any())
            return;

        _xAxisLabels = Data.Points
            .Select(p => FormatTimestamp(p.Timestamp))
            .ToArray();

        var chartSeries = new List<ChartSeries>
        {
            new()
            {
                Name = "Count",
                Data = Data.Points.Select(p => (double)(p.Count ?? 0)).ToArray(),
                ShowDataMarkers = true
            }
        };

        if (Data.Points.Any(p => p.Sum.HasValue))
        {
            chartSeries.Add(new ChartSeries
            {
                Name = "Sum",
                Data = Data.Points.Select(p => p.Sum ?? double.NaN).ToArray(),
                ShowDataMarkers = true
            });
        }

        if (Data.Points.Any(p => p.Min.HasValue))
        {
            chartSeries.Add(new ChartSeries
            {
                Name = "Min",
                Data = Data.Points.Select(p => p.Min ?? double.NaN).ToArray(),
                ShowDataMarkers = true
            });
        }

        if (Data.Points.Any(p => p.Max.HasValue))
        {
            chartSeries.Add(new ChartSeries
            {
                Name = "Max",
                Data = Data.Points.Select(p => p.Max ?? double.NaN).ToArray(),
                ShowDataMarkers = true
            });
        }

        _chartSeries = chartSeries;

        _chartOptions = new ChartOptions
        {
            YAxisTicks = 5,
            ChartPalette = new[]
            {
                Colors.Blue.Default,
                Colors.Green.Default,
                Colors.Orange.Default,
                Colors.Red.Default
            },
            LineStrokeWidth = 2,
            InterpolationOption = InterpolationOption.Straight
        };
    }

    private void BuildSummaryQuantileChartData()
    {
        if (Data?.Points == null || !Data.Points.Any())
            return;

        _xAxisLabels = Data.Points
            .Select(p => FormatTimestamp(p.Timestamp))
            .ToArray();

        var quantiles = Data.Points
            .Where(p => p.Quantiles != null)
            .SelectMany(p => p.Quantiles!)
            .Distinct()
            .OrderBy(q => q)
            .ToList();

        if (!quantiles.Any())
        {
            _canShowChart = false;
            return;
        }

        _chartSeries = new List<ChartSeries>();

        foreach (var quantile in quantiles)
        {
            var data = new double[Data.Points.Count];
            Array.Fill(data, double.NaN);

            for (int i = 0; i < Data.Points.Count; i++)
            {
                var point = Data.Points[i];
                if (point.Quantiles == null || point.QuantileValues == null)
                    continue;

                for (int j = 0; j < point.Quantiles.Count && j < point.QuantileValues.Count; j++)
                {
                    if (Math.Abs(point.Quantiles[j] - quantile) < 0.0000001)
                    {
                        data[i] = point.QuantileValues[j];
                        break;
                    }
                }
            }

            _chartSeries.Add(new ChartSeries
            {
                Name = $"P{quantile * 100:F0}",
                Data = data,
                ShowDataMarkers = true
            });
        }

        _chartOptions = new ChartOptions
        {
            YAxisTicks = 5,
            ChartPalette = new[]
            {
                Colors.Blue.Default,
                Colors.Green.Default,
                Colors.Red.Default,
                Colors.Orange.Default,
                Colors.Purple.Default,
                Colors.Teal.Default
            },
            LineStrokeWidth = 2,
            InterpolationOption = InterpolationOption.Straight
        };
    }

    private void BuildMultiSeriesChartData()
    {
        if (MultiSeriesData?.Series == null || !MultiSeriesData.Series.Any())
            return;

        // Collect union of all timestamps across all series
        var allTimestamps = MultiSeriesData.Series
            .SelectMany(s => s.Points.Select(p => p.Timestamp))
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        if (!allTimestamps.Any())
        {
            _canShowChart = false;
            return;
        }

        // Build timestamp index for fast lookup
        var timestampIndex = new Dictionary<DateTime, int>();
        for (int i = 0; i < allTimestamps.Count; i++)
            timestampIndex[allTimestamps[i]] = i;

        // Format X-axis labels
        _xAxisLabels = allTimestamps
            .Select(t => FormatTimestamp(t, allTimestamps))
            .ToArray();

        // Build one ChartSeries per service
        _chartSeries = new List<ChartSeries>();
        foreach (var series in MultiSeriesData.Series)
        {
            var values = new double[allTimestamps.Count];
            Array.Fill(values, double.NaN);

            foreach (var point in series.Points)
            {
                if (timestampIndex.TryGetValue(point.Timestamp, out var idx))
                {
                    values[idx] = (double)(point.DoubleValue ?? point.IntValue ?? 0);
                }
            }

            _chartSeries.Add(new ChartSeries
            {
                Name = series.SeriesName,
                Data = values,
                ShowDataMarkers = true
            });
        }

        // Configure chart options with distinguishable colors
        var allValues = _chartSeries
            .SelectMany(s => s.Data)
            .Where(v => !double.IsNaN(v))
            .ToList();

        var maxValue = allValues.Any() ? allValues.Max() : 0;
        var minValue = allValues.Any() ? allValues.Min() : 0;
        var range = maxValue - minValue;

        _chartOptions = new ChartOptions
        {
            YAxisTicks = range > 0 ? 5 : 1,
            ChartPalette = new[]
            {
                Colors.Blue.Default, Colors.Green.Default, Colors.Red.Default,
                Colors.Orange.Default, Colors.Purple.Default, Colors.Teal.Default,
                Colors.Pink.Default, Colors.Amber.Default, Colors.Cyan.Default,
                Colors.DeepPurple.Default
            },
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
        return FormatByTimeSpan(timestamp, timeSpan);
    }

    private string FormatTimestamp(DateTime timestamp, List<DateTime> allTimestamps)
    {
        var timeSpan = allTimestamps.Last() - allTimestamps.First();
        return FormatByTimeSpan(timestamp, timeSpan);
    }

    private static string FormatByTimeSpan(DateTime timestamp, TimeSpan timeSpan)
    {
        timestamp = timestamp.ToLocalTime();

        if (timeSpan.TotalHours < 1)
            return timestamp.ToString("HH:mm:ss");
        if (timeSpan.TotalHours < 24)
            return timestamp.ToString("HH:mm");
        return timestamp.ToString("MM/dd HH:mm");
    }
}
