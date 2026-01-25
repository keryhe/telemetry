using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Client.Models;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using System.Text;

namespace Keryhe.Telemetry.Client.Components.Pages;

public partial class MetricDetail : ComponentBase, IDisposable
{
    [Parameter]
    public string MetricName { get; set; } = "";

    [Inject]
    private IMetricService MetricService { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    private MetricSeries? _metricData;
    private MetricInfo? _metricInfo;
    private Dictionary<string, List<string>>? _labels;
    private List<BreadcrumbItem> _breadcrumbItems = new();
    private bool _loading = false;
    private bool _autoRefresh = false;
    private TimeRange _selectedTimeRange = TimeRange.Last1Hour;
    private Timer? _refreshTimer;
    private int _refreshInterval = 30;
    private string? _serviceName;

    // Stats
    private bool _canShowStats = false;
    private double? _currentValue;
    private double? _minValue;
    private double? _maxValue;
    private double? _avgValue;

    protected override async Task OnInitializedAsync()
    {
        SetupBreadcrumbs();
        await LoadDataAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_selectedTimeRange != TimeRange.Custom)
        {
            _refreshInterval = _selectedTimeRange.GetRecommendedRefreshInterval();
        }

        // Reload data when time range changes
        await LoadDataAsync();
        
        // Update timer interval if auto-refresh is enabled
        if (_autoRefresh)
        {
            RestartRefreshTimer();
        }
    }

    private async Task LoadDataAsync()
    {
        _loading = true;
        StateHasChanged();

        try
        {
            var (start, end) = _selectedTimeRange.ToDateTimeRange();

            // Load metric series data
            _metricData = await MetricService.GetMetricSeriesAsync(
                Uri.UnescapeDataString(MetricName), 
                start, 
                end);

            if (_metricData != null)
            {
                // Try to get metric info to get additional details
                var metrics = await MetricService.GetMetricsByNameAsync(Uri.UnescapeDataString(MetricName));
                _metricInfo = metrics.FirstOrDefault();
                _serviceName = _metricInfo?.ServiceName;

                // Load labels
                _labels = await MetricService.GetMetricLabelsAsync(Uri.UnescapeDataString(MetricName));

                // Calculate statistics
                CalculateStats();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading metric data: {ex.Message}");
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    private void CalculateStats()
    {
        if (_metricData?.Points == null || !_metricData.Points.Any())
        {
            _canShowStats = false;
            return;
        }

        // Only show stats for GAUGE and SUM types
        _canShowStats = _metricData.Type == MetricType.GAUGE || _metricData.Type == MetricType.SUM;

        if (_canShowStats)
        {
            var values = _metricData.Points
                .Select(p => (double?)(p.DoubleValue ?? p.IntValue ?? 0))
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

            if (values.Any())
            {
                _currentValue = values.Last();
                _minValue = values.Min();
                _maxValue = values.Max();
                _avgValue = values.Average();
            }
        }
        else
        {
            _currentValue = null;
            _minValue = null;
            _maxValue = null;
            _avgValue = null;
        }
    }

    private async Task RefreshDataAsync()
    {
        await LoadDataAsync();
    }

    private async Task ExportDataAsync()
    {
        if (_metricData?.Points == null || !_metricData.Points.Any())
            return;

        try
        {
            var csv = new StringBuilder();
            csv.AppendLine("Timestamp,Value");

            foreach (var point in _metricData.Points)
            {
                var value = point.DoubleValue ?? point.IntValue ?? 0;
                csv.AppendLine($"{point.Timestamp:yyyy-MM-dd HH:mm:ss},{value}");
            }

            var fileName = $"{Uri.UnescapeDataString(MetricName)}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            var base64 = Convert.ToBase64String(bytes);

            await JSRuntime.InvokeVoidAsync("downloadFile", fileName, base64);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error exporting data: {ex.Message}");
        }
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
                    await LoadDataAsync();
                });
            }, null, TimeSpan.FromSeconds(_refreshInterval), TimeSpan.FromSeconds(_refreshInterval));
        }
    }

    private void SetupBreadcrumbs()
    {
        _breadcrumbItems = new List<BreadcrumbItem>
        {
            new BreadcrumbItem("Home", href: "/", icon: Icons.Material.Filled.Home),
            new BreadcrumbItem("Metrics", href: "/metrics", icon: Icons.Material.Filled.BarChart),
            new BreadcrumbItem(Uri.UnescapeDataString(MetricName), href: null, disabled: true)
        };
    }

    private string FormatValue(double? value)
    {
        if (!value.HasValue)
            return "N/A";

        var unit = _metricInfo?.Unit ?? "";

        // Format based on magnitude
        if (Math.Abs(value.Value) >= 1_000_000)
            return $"{value.Value / 1_000_000:F2}M {unit}";
        if (Math.Abs(value.Value) >= 1_000)
            return $"{value.Value / 1_000:F2}K {unit}";

        return $"{value.Value:F2} {unit}";
    }

    private Color GetMetricTypeColor(MetricType type) => type switch
    {
        MetricType.GAUGE => Color.Primary,
        MetricType.SUM => Color.Success,
        MetricType.HISTOGRAM => Color.Warning,
        MetricType.EXPONENTIAL_HISTOGRAM => Color.Warning,
        MetricType.SUMMARY => Color.Info,
        _ => Color.Default
    };

    protected string FormatAttributeValue(object value)
    {
        if (value == null) return "null";
        if (value is System.Text.Json.JsonElement element)
        {
            return element.ToString();
        }
        return value.ToString() ?? "";
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}
