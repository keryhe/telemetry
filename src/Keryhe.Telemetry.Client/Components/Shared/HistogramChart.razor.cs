using ApexCharts;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;

namespace Keryhe.Telemetry.Client.Components.Shared;

public partial class HistogramChart : ComponentBase
{
    [Parameter]
    public MetricSeries? Data { get; set; }

    [Parameter]
    public int Height { get; set; } = 400;

    private List<BucketData>? _bucketData;
    private ApexChartOptions<BucketData> _chartOptions = new()
    {
        Chart = new Chart { Toolbar = new Toolbar { Show = false } },
    };

    protected override void OnParametersSet()
    {
        if (Data?.Points == null || !Data.Points.Any())
        {
            _bucketData = null;
            return;
        }

        // Get the latest histogram data point
        var latestPoint = Data.Points
            .Where(p => p.BucketCounts != null && p.BucketBounds != null)
            .OrderByDescending(p => p.Timestamp)
            .FirstOrDefault();

        if (latestPoint?.BucketCounts == null || latestPoint.BucketBounds == null)
        {
            _bucketData = null;
            return;
        }

        _bucketData = new List<BucketData>();

        for (int i = 0; i < latestPoint.BucketCounts.Count; i++)
        {
            string label;
            
            if (i == 0)
            {
                // First bucket: < first bound
                label = $"< {latestPoint.BucketBounds[0]:F2}";
            }
            else if (i < latestPoint.BucketBounds.Count)
            {
                // Middle buckets: [lower, upper)
                label = $"{latestPoint.BucketBounds[i - 1]:F2} - {latestPoint.BucketBounds[i]:F2}";
            }
            else
            {
                // Last bucket: >= last bound
                label = $">= {latestPoint.BucketBounds[^1]:F2}";
            }

            _bucketData.Add(new BucketData
            {
                BucketLabel = label,
                Count = latestPoint.BucketCounts[i],
                Order = i
            });
        }
    }

    public class BucketData
    {
        public string BucketLabel { get; set; } = "";
        public long Count { get; set; }
        public int Order { get; set; }
    }
}
