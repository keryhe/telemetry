using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Client.Services.State;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Keryhe.Telemetry.Client.Components.Pages;

public partial class Metrics : ComponentBase, IDisposable
{
    private List<MetricInfo> _allMetrics = new();
    private List<MetricInfo> _filteredMetrics = new();
    private List<string> _availableServices = new();
    private List<UniqueMetricSummary> _uniqueMetricSummaries = new();
    private List<UniqueMetricSummary> _filteredUniqueMetrics = new();
    private bool _loading = false;
    private string _searchText = "";
    private string? _selectedService = null;
    private MetricType? _selectedType = null;
    private bool _showUniqueNamesView = true;

    // Stats
    private int _uniqueMetricCount = 0;
    private int _gaugeCount = 0;
    private int _counterCount = 0;
    private int _servicesCount = 0;

    [Inject]
    private IMetricService MetricService { get; set; } = null!;

    [Inject]
    private MetricsPageState State { get; set; } = null!;

    [Inject]
    private TimeRangeState TimeRangeState { get; set; } = null!;

    protected override async Task OnInitializedAsync()
    {
        TimeRangeState.OnChange += OnTimeRangeChanged;

        _searchText = State.SearchText;
        _selectedService = State.SelectedService;
        _selectedType = State.SelectedType;
        _showUniqueNamesView = State.ShowUniqueNamesView;

        await LoadMetricsAsync();
        await LoadServicesAsync();
    }

    private void OnTimeRangeChanged()
    {
        _ = InvokeAsync(async () =>
        {
            await LoadMetricsAsync();
            await LoadServicesAsync();
        });
    }

    private async Task LoadMetricsAsync()
    {
        _loading = true;
        StateHasChanged();

        try
        {
            var (start, end) = TimeRangeState.GetDateTimeRange();
            _allMetrics = await MetricService.GetAllMetricsAsync(limit: 500, startTime: start, endTime: end);
            BuildUniqueMetricSummaries();
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
        _availableServices = _allMetrics
            .Select(m => m.ServiceName)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s)
            .Cast<string>()
            .ToList();

        if (!string.IsNullOrEmpty(_selectedService) &&
            !_availableServices.Contains(_selectedService, StringComparer.OrdinalIgnoreCase))
        {
            _selectedService = null;
        }
    }

    private void BuildUniqueMetricSummaries()
    {
        _uniqueMetricSummaries = _allMetrics
            .GroupBy(m => m.Name)
            .Select(g => new UniqueMetricSummary
            {
                Name = g.Key,
                Type = g.First().Type,
                Unit = g.First().Unit,
                Description = g.First().Description,
                InstanceCount = g.Count(),
                Services = g.Select(m => m.ServiceName ?? "unknown")
                    .Distinct()
                    .OrderBy(s => s)
                    .ToList(),
                LastSeen = g.Max(m => m.LastSeen)
            })
            .OrderBy(m => m.Name)
            .ToList();
    }

    private void ToggleView()
    {
        _showUniqueNamesView = !_showUniqueNamesView;
        State.ShowUniqueNamesView = _showUniqueNamesView;
        StateHasChanged();
    }

    private void ApplyFilters()
    {
        State.SearchText = _searchText;
        State.SelectedService = _selectedService;
        State.SelectedType = _selectedType;
        State.ShowUniqueNamesView = _showUniqueNamesView;

        // Filter flat list
        _filteredMetrics = _allMetrics;

        if (!string.IsNullOrEmpty(_selectedService))
        {
            _filteredMetrics = _filteredMetrics
                .Where(m => m.ServiceName == _selectedService)
                .ToList();
        }

        if (_selectedType.HasValue)
        {
            _filteredMetrics = _filteredMetrics
                .Where(m => m.Type == _selectedType.Value)
                .ToList();
        }

        if (!string.IsNullOrEmpty(_searchText))
        {
            _filteredMetrics = _filteredMetrics
                .Where(m => m.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                           (m.Description?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        // Filter unique metric summaries
        _filteredUniqueMetrics = _uniqueMetricSummaries;

        if (!string.IsNullOrEmpty(_selectedService))
        {
            _filteredUniqueMetrics = _filteredUniqueMetrics
                .Where(m => m.Services.Contains(_selectedService))
                .ToList();
        }

        if (_selectedType.HasValue)
        {
            _filteredUniqueMetrics = _filteredUniqueMetrics
                .Where(m => m.Type == _selectedType.Value)
                .ToList();
        }

        if (!string.IsNullOrEmpty(_searchText))
        {
            _filteredUniqueMetrics = _filteredUniqueMetrics
                .Where(m => m.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                           (m.Description?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
    }

    private void CalculateStats()
    {
        _uniqueMetricCount = _uniqueMetricSummaries.Count;
        _gaugeCount = _uniqueMetricSummaries.Count(m => m.Type == MetricType.GAUGE);
        _counterCount = _uniqueMetricSummaries.Count(m => m.Type == MetricType.SUM);
        _servicesCount = _allMetrics
            .Select(m => m.ServiceName)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .Count();
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

    public void Dispose()
    {
        TimeRangeState.OnChange -= OnTimeRangeChanged;
    }
}
