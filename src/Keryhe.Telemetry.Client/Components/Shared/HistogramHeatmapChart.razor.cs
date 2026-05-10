using ApexCharts;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;

namespace Keryhe.Telemetry.Client.Components.Shared;

public partial class HistogramHeatmapChart : ComponentBase
{
    [Parameter]
    public MetricSeries? Data { get; set; }

    [Parameter]
    public int Height { get; set; } = 320;

    private record HeatmapPoint(DateTime Time, double Value);

    private List<(string BucketLabel, List<HeatmapPoint> Points)> _heatSeries = new();
    private ApexChartOptions<HeatmapPoint> _chartOptions = new();
    private bool _canShow = false;

    protected override void OnParametersSet()
    {
        _heatSeries = new();
        _canShow = false;

        if (Data?.Points == null || !Data.Points.Any()) return;

        switch (Data.Type)
        {
            case MetricType.HISTOGRAM:
                BuildExplicitHeatmap();
                break;
            case MetricType.EXPONENTIAL_HISTOGRAM:
                BuildExponentialHeatmap();
                break;
        }

        if (_heatSeries.Any())
        {
            BuildChartOptions();
            _canShow = true;
        }
    }

    private void BuildExplicitHeatmap()
    {
        // Use the first point with bucket data to define the bucket labels
        var template = Data!.Points
            .FirstOrDefault(p => p.BucketCounts != null && p.BucketBounds != null);
        if (template == null) return;

        var bounds = template.BucketBounds!;
        var bucketCount = template.BucketCounts!.Count;

        // Build ordered label list
        var labels = new List<string>(bucketCount);
        for (int i = 0; i < bucketCount; i++)
        {
            if (i == 0)
                labels.Add($"< {bounds[0]:G6}");
            else if (i < bounds.Count)
                labels.Add($"{bounds[i - 1]:G6} – {bounds[i]:G6}");
            else
                labels.Add($">= {bounds[^1]:G6}");
        }

        // Initialise empty point lists per bucket
        var seriesMap = labels.Select(l => (l, new List<HeatmapPoint>())).ToList();

        foreach (var point in Data.Points.OrderBy(p => p.Timestamp))
        {
            if (point.BucketCounts == null || point.BucketCounts.Count != bucketCount) continue;

            for (int i = 0; i < bucketCount; i++)
                seriesMap[i].Item2.Add(new HeatmapPoint(point.Timestamp, (double)point.BucketCounts[i]));
        }

        _heatSeries = seriesMap;
    }

    private void BuildExponentialHeatmap()
    {
        // Find first point with positive bucket data to define labels
        var template = Data!.Points
            .FirstOrDefault(p => p.PositiveBucketCounts != null && p.Scale.HasValue);
        if (template == null) return;

        int scale = template.Scale!.Value;
        int offset = template.PositiveOffset ?? 0;
        int bucketCount = template.PositiveBucketCounts!.Count;

        // base = 2^(2^-scale)
        double log2Base = Math.Pow(2.0, -scale);

        // Build bucket labels: [base^(offset+i), base^(offset+i+1))
        var labels = new List<string>();

        // Zero bucket first
        labels.Add("= 0");

        for (int i = 0; i < bucketCount; i++)
        {
            double lo = Math.Pow(2.0, log2Base * (offset + i));
            double hi = Math.Pow(2.0, log2Base * (offset + i + 1));
            labels.Add($"{lo:G4} – {hi:G4}");
        }

        int totalBuckets = labels.Count; // zero + positive buckets

        var seriesMap = labels.Select(l => (l, new List<HeatmapPoint>())).ToList();

        foreach (var point in Data.Points.OrderBy(p => p.Timestamp))
        {
            if (point.PositiveBucketCounts == null || point.PositiveBucketCounts.Count != bucketCount) continue;

            // Index 0 = zero bucket
            seriesMap[0].Item2.Add(new HeatmapPoint(point.Timestamp, (double)(point.ZeroCount ?? 0)));

            for (int i = 0; i < bucketCount; i++)
                seriesMap[i + 1].Item2.Add(new HeatmapPoint(point.Timestamp, (double)point.PositiveBucketCounts[i]));
        }

        // Only keep series that have data
        _heatSeries = seriesMap.Where(s => s.Item2.Any()).ToList();
    }

    private void BuildChartOptions()
    {
        // Compute the max count across all buckets for a consistent color scale
        double maxCount = _heatSeries
            .SelectMany(s => s.Item2)
            .Select(p => p.Value)
            .DefaultIfEmpty(0)
            .Max();

        _chartOptions = new ApexChartOptions<HeatmapPoint>
        {
            Chart = new Chart
            {
                Toolbar = new Toolbar { Show = false },
                Zoom = new Zoom { Enabled = false }
            },
            Xaxis = new XAxis { Type = XAxisType.Datetime },
            PlotOptions = new PlotOptions
            {
                Heatmap = new PlotOptionsHeatmap
                {
                    ColorScale = new PlotOptionsHeatmapColorScale
                    {
                        Ranges = new List<PlotOptionsHeatmapColorScaleRange>
                        {
                            new() { From = 0, To = 0, Color = "#ECEFF1", Name = "0" },
                            new() { From = 0.001, To = maxCount * 0.25, Color = "#90CAF9", Name = "Low" },
                            new() { From = maxCount * 0.25, To = maxCount * 0.6, Color = "#1976D2", Name = "Medium" },
                            new() { From = maxCount * 0.6, To = maxCount, Color = "#0D47A1", Name = "High" },
                        }
                    }
                }
            },
            DataLabels = new DataLabels { Enabled = false },
            Legend = new Legend { Show = false },
        };
    }
}
