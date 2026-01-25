using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Keryhe.Telemetry.Client.Components.Pages;

public partial class Metrics : ComponentBase
{
    private List<MetricInfo> _allMetrics = new();
    private List<MetricInfo> _filteredMetrics = new();
    private List<string> _availableServices = new();
    private List<string> _uniqueMetricNames = new();
    private bool _loading = false;
    private string _searchText = "";
    private string? _selectedService = null;
    private MetricType? _selectedType = null;
    private bool _showUniqueNamesView = false;
    
    // Stats
    private int _totalMetrics = 0;
    private int _gaugeCount = 0;
    private int _counterCount = 0;
    private int _servicesCount = 0;

    [Inject]
    private IMetricService MetricService { get; set; } = null!;

    protected override async Task OnInitializedAsync()
    {
        await LoadMetricsAsync();
        await LoadServicesAsync();
        await LoadUniqueMetricNamesAsync();
    }

    private async Task LoadMetricsAsync()
    {
        _loading = true;
        StateHasChanged();

        try
        {
            _allMetrics = await MetricService.GetAllMetricsAsync(limit: 500);
            ApplyFilters();
            CalculateStats();
        }
        finally
        {
            _loading = false;
            StateHasChanged();
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
        }
        catch (Exception)
        {
            _availableServices = new List<string>();
        }
    }

    private async Task LoadUniqueMetricNamesAsync()
    {
        try
        {
            _uniqueMetricNames = await MetricService.GetUniqueMetricNamesAsync(_selectedService);
        }
        catch (Exception)
        {
            _uniqueMetricNames = new List<string>();
        }
    }

    private void ToggleView()
    {
        _showUniqueNamesView = !_showUniqueNamesView;
        StateHasChanged();
    }

    private void ApplyFilters()
    {
        _filteredMetrics = _allMetrics;

        // Filter by service
        if (!string.IsNullOrEmpty(_selectedService))
        {
            _filteredMetrics = _filteredMetrics
                .Where(m => m.ServiceName == _selectedService)
                .ToList();
        }

        // Filter by type
        if (_selectedType.HasValue)
        {
            _filteredMetrics = _filteredMetrics
                .Where(m => m.Type == _selectedType.Value)
                .ToList();
        }

        // Filter by search text
        if (!string.IsNullOrEmpty(_searchText))
        {
            _filteredMetrics = _filteredMetrics
                .Where(m => m.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                           (m.Description?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
    }

    private void CalculateStats()
    {
        _totalMetrics = _allMetrics.Count;
        _gaugeCount = _allMetrics.Count(m => m.Type == MetricType.GAUGE);
        _counterCount = _allMetrics.Count(m => m.Type == MetricType.SUM);
        _servicesCount = _allMetrics
            .Select(m => m.ServiceName)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .Count();
    }

    private int GetMetricInstanceCount(string metricName)
    {
        return _allMetrics.Count(m => m.Name == metricName);
    }

    private List<string> GetServicesForMetric(string metricName)
    {
        return _allMetrics
            .Where(m => m.Name == metricName)
            .Select(m => m.ServiceName ?? "unknown")
            .Distinct()
            .OrderBy(s => s)
            .ToList();
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

    private string FormatDateTime(DateTime dateTime)
    {
        return dateTime.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
