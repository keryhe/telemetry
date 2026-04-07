using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Client.Services.State;
using Keryhe.Telemetry.Client.Models;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;

namespace Keryhe.Telemetry.Client.Components.Pages;

public partial class ServiceMetrics : ComponentBase, IDisposable
{
    [Inject]
    private IMetricService MetricService { get; set; } = default!;

    [Inject]
    private ServiceMetricsPageState State { get; set; } = default!;

    [Inject]
    private TimeRangeState TimeRangeState { get; set; } = default!;

    private List<string> _availableServices = new();
    private string? _selectedService = null;
    private bool _loading = false;
    private bool _autoRefresh = false;
    private Timer? _refreshTimer;
    private int _refreshIntervalSeconds = 30;
    private int _loadedCount = 0;
    private int _totalCount = 0;

    private List<MetricCardData> _metrics = new();
    private List<MetricCardData> _gaugeMetrics = new();
    private List<MetricCardData> _counterMetrics = new();
    private List<MetricCardData> _otherMetrics = new();

    protected override async Task OnInitializedAsync()
    {
        TimeRangeState.OnChange += OnTimeRangeChanged;
        _autoRefresh = State.AutoRefresh;
        _selectedService = State.SelectedService;
        _refreshIntervalSeconds = TimeRangeState.SelectedTimeRange.GetRecommendedRefreshInterval();
        await LoadServicesAsync();
    }

    private void OnTimeRangeChanged()
    {
        _refreshIntervalSeconds = TimeRangeState.SelectedTimeRange.GetRecommendedRefreshInterval();
        RestartRefreshTimer();
        _ = InvokeAsync(LoadMetricsAsync);
    }

    protected override void OnParametersSet()
    {
        if (_autoRefresh)
        {
            RestartRefreshTimer();
        }
        else
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }
    }

    private async Task LoadServicesAsync()
    {
        try
        {
            var summaries = await MetricService.GetServiceMetricSummariesAsync();
            _availableServices = summaries
                .Select(s => s.ServiceName)
                .Where(s => !string.IsNullOrEmpty(s))
                .OrderBy(s => s)
                .ToList();

            // Clear saved service if it no longer exists in the list
            if (!string.IsNullOrEmpty(_selectedService) && !_availableServices.Contains(_selectedService))
            {
                _selectedService = null;
            }

            // Auto-select first service if available
            if (_availableServices.Any() && string.IsNullOrEmpty(_selectedService))
            {
                _selectedService = _availableServices.First();
                await LoadMetricsAsync();
            }
            else if (!string.IsNullOrEmpty(_selectedService))
            {
                await LoadMetricsAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading services: {ex.Message}");
        }
    }

    private async Task LoadMetricsAsync()
    {
        if (string.IsNullOrEmpty(_selectedService))
            return;

        State.SelectedService = _selectedService;

        _loading = true;
        _loadedCount = 0;
        StateHasChanged();

        try
        {
            // Get all metrics for the selected service
            var (startTime, endTime) = TimeRangeState.GetDateTimeRange();
            var metricInfos = await MetricService.GetMetricsByServiceAsync(_selectedService, startTime, endTime);
            _totalCount = metricInfos.Count;

            // Load metric series data for each metric SEQUENTIALLY to avoid EF Core concurrency issues
            var metricDataList = new List<MetricCardData>();

            foreach (var info in metricInfos)
            {
                try
                {
                    var series = await MetricService.GetMetricSeriesAsync(info.Name, null, startTime, endTime);
                    metricDataList.Add(new MetricCardData
                    {
                        MetricName = info.Name,
                        Unit = info.Unit,
                        Type = info.Type,
                        DataPoints = series?.Points ?? new List<MetricDataPoint>(),
                        CurrentValue = CalculateCurrentValue(series?.Points),
                        MinValue = CalculateMinValue(series?.Points),
                        MaxValue = CalculateMaxValue(series?.Points),
                        AvgValue = CalculateAvgValue(series?.Points),
                        PreviousValue = CalculatePreviousValue(series?.Points)
                    });
                    
                    _loadedCount++;
                    StateHasChanged(); // Update UI with progress
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading metric {info.Name}: {ex.Message}");
                    // Add empty data if metric series fails
                    metricDataList.Add(new MetricCardData
                    {
                        MetricName = info.Name,
                        Unit = info.Unit,
                        Type = info.Type,
                        DataPoints = new List<MetricDataPoint>()
                    });
                    
                    _loadedCount++;
                    StateHasChanged();
                }
            }

            _metrics = metricDataList;

            // Group metrics by type
            _gaugeMetrics = _metrics.Where(m => m.Type == MetricType.GAUGE).ToList();
            _counterMetrics = _metrics.Where(m => m.Type == MetricType.SUM).ToList();
            _otherMetrics = _metrics.Where(m => m.Type != MetricType.GAUGE && m.Type != MetricType.SUM).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading metrics: {ex.Message}");
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    private async Task RefreshDataAsync()
    {
        await LoadMetricsAsync();
    }

    private void OnAutoRefreshChanged(bool value)
    {
        _autoRefresh = value;
        State.AutoRefresh = value;
        RestartRefreshTimer();
    }

    private void RestartRefreshTimer()
    {
        _refreshTimer?.Dispose();

        if (_autoRefresh)
        {
            _refreshTimer = new Timer(async _ =>
            {
                await InvokeAsync(async () =>
                {
                    await LoadMetricsAsync();
                });
            }, null, TimeSpan.FromSeconds(_refreshIntervalSeconds), TimeSpan.FromSeconds(_refreshIntervalSeconds));
        }
    }

    private double? CalculateCurrentValue(List<MetricDataPoint>? points)
    {
        if (points == null || !points.Any())
            return null;

        var lastPoint = points.Last();
        return lastPoint.DoubleValue ?? lastPoint.IntValue ?? 0;
    }

    private double? CalculatePreviousValue(List<MetricDataPoint>? points)
    {
        if (points == null || points.Count < 2)
            return null;

        var previousPoint = points[points.Count - 2];
        return previousPoint.DoubleValue ?? previousPoint.IntValue ?? 0;
    }

    private double? CalculateMinValue(List<MetricDataPoint>? points)
    {
        if (points == null || !points.Any())
            return null;

        return points
            .Select(p => (double?)(p.DoubleValue ?? p.IntValue ?? 0))
            .Where(v => v.HasValue)
            .Min();
    }

    private double? CalculateMaxValue(List<MetricDataPoint>? points)
    {
        if (points == null || !points.Any())
            return null;

        return points
            .Select(p => (double?)(p.DoubleValue ?? p.IntValue ?? 0))
            .Where(v => v.HasValue)
            .Max();
    }

    private double? CalculateAvgValue(List<MetricDataPoint>? points)
    {
        if (points == null || !points.Any())
            return null;

        return points
            .Select(p => (double?)(p.DoubleValue ?? p.IntValue ?? 0))
            .Where(v => v.HasValue)
            .Average();
    }

    public void Dispose()
    {
        TimeRangeState.OnChange -= OnTimeRangeChanged;
        _refreshTimer?.Dispose();
    }

    // Helper class to hold metric card data
    private class MetricCardData
    {
        public string MetricName { get; set; } = "";
        public string? Unit { get; set; }
        public MetricType Type { get; set; }
        public List<MetricDataPoint> DataPoints { get; set; } = new();
        public double? CurrentValue { get; set; }
        public double? PreviousValue { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public double? AvgValue { get; set; }
    }
}
