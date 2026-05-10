using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Client.Services.State;
using Keryhe.Telemetry.Client.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using System.Text;
using System.Text.Json;

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

    [Inject]
    private MetricDetailPageState State { get; set; } = default!;

    [Inject]
    private TimeRangeState TimeRangeState { get; set; } = default!;

    private MetricSeries? _metricData;
    private MetricInfo? _metricInfo;
    private Dictionary<string, List<string>>? _labels;
    private List<BreadcrumbItem> _breadcrumbItems = new();
    private bool _loading = false;
    private bool _autoRefresh = false;
    private Timer? _refreshTimer;
    private int _refreshInterval = 30;
    private string? _serviceName;
    private bool _initialized = false;

    private bool _stateLoaded = false;

    // Active tab with state persistence
    private int _activeTabBacking = 0;
    private int _activeTab
    {
        get => _activeTabBacking;
        set { _activeTabBacking = value; State.ActiveTab = value; _ = State.SaveAsync(); }
    }

    // Filtering
    private List<MetricInfo> _allInstances = new();
    private List<string> _availableServices = new();
    private string? _selectedServiceFilter = null;
    private Dictionary<string, string> _selectedLabelFilters = new();

    // Multi-series chart
    private MultiSeriesMetricData? _multiSeriesData;
    private bool _showPerServiceView = false;

    // Counter state
    private bool _isCounterMetric = false;
    private bool _showRaw = false;
    private bool _statsAreRates = false;
    private bool _statsAreHistogram = false;

    // Stats
    private bool _canShowStats = false;
    private double? _currentValue;
    private double? _minValue;
    private double? _maxValue;
    private double? _avgValue;

    protected override async Task OnInitializedAsync()
    {
        TimeRangeState.OnChange += OnTimeRangeChanged;
        SetupBreadcrumbs();

        var decodedMetricName = Uri.UnescapeDataString(MetricName);

        if (State.CurrentMetricName == decodedMetricName)
        {
            // Same metric: restore all state
            _autoRefresh = State.AutoRefresh;
            _selectedServiceFilter = State.SelectedServiceFilter;
            _selectedLabelFilters = new Dictionary<string, string>(State.SelectedLabelFilters);
            _showPerServiceView = State.ShowPerServiceView;
            _activeTabBacking = State.ActiveTab;
            _showRaw = State.ShowRaw;
        }
        else
        {
            // Different metric: carry over auto-refresh only
            _autoRefresh = State.AutoRefresh;
            _selectedServiceFilter = null;
            _selectedLabelFilters = new();
            _showPerServiceView = false;
            _activeTabBacking = 0;
            _showRaw = false;
            State.CurrentMetricName = decodedMetricName;
        }

        _refreshInterval = TimeRangeState.SelectedTimeRange.GetRecommendedRefreshInterval();

        await LoadDataAsync();
        _initialized = true;

        if (_autoRefresh)
            RestartRefreshTimer();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _stateLoaded) return;
        _stateLoaded = true;

        await State.LoadAsync();
        if (State.CurrentMetricName == Uri.UnescapeDataString(MetricName))
        {
            _selectedServiceFilter = State.SelectedServiceFilter;
            _selectedLabelFilters = new Dictionary<string, string>(State.SelectedLabelFilters);
            _showPerServiceView = State.ShowPerServiceView;
            _activeTabBacking = State.ActiveTab;
            _showRaw = State.ShowRaw;
        }
        StateHasChanged();
    }

    private void OnTimeRangeChanged()
    {
        _ = InvokeAsync(async () =>
        {
            _refreshInterval = TimeRangeState.SelectedTimeRange.GetRecommendedRefreshInterval();
            // Auto-refresh is meaningless on a frozen custom/zoomed range — pause it
            if (TimeRangeState.SelectedTimeRange == TimeRange.Custom)
            {
                _autoRefresh = false;
                State.AutoRefresh = false;
            }
            RestartRefreshTimer();
            await LoadDataAsync();
        });
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_initialized) return;

        var decodedMetricName = Uri.UnescapeDataString(MetricName);

        if (State.CurrentMetricName != decodedMetricName)
        {
            // Navigated to a different metric
            _selectedServiceFilter = null;
            _selectedLabelFilters = new();
            _showPerServiceView = false;
            _activeTabBacking = 0;
            State.CurrentMetricName = decodedMetricName;
        }

        _refreshInterval = TimeRangeState.SelectedTimeRange.GetRecommendedRefreshInterval();

        await LoadDataAsync();

        if (_autoRefresh)
        {
            RestartRefreshTimer();
        }
    }

    private async Task LoadDataAsync()
    {
        // Persist current filter state
        State.AutoRefresh = _autoRefresh;
        State.SelectedServiceFilter = _selectedServiceFilter;
        State.SelectedLabelFilters = new Dictionary<string, string>(_selectedLabelFilters);
        State.ShowPerServiceView = _showPerServiceView;
        State.ShowRaw = _showRaw;
        _ = State.SaveAsync();

        _loading = true;
        StateHasChanged();

        try
        {
            var decodedName = Uri.UnescapeDataString(MetricName);
            var (start, end) = TimeRangeState.GetDateTimeRange();

            // Load all instances of this metric
            _allInstances = await MetricService.GetMetricsByNameAsync(decodedName);
            _availableServices = _allInstances
                .Select(m => m.ServiceName ?? "unknown")
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            var orderedInstances = _allInstances
                .OrderByDescending(m => m.LastSeen)
                .ThenByDescending(m => m.Id)
                .ToList();

            // Build datapoint label filters only (service scope is resolved via metric instance).
            var labelFilters = new Dictionary<string, string>(_selectedLabelFilters);
            var metricForQuery = !string.IsNullOrEmpty(_selectedServiceFilter)
                ? orderedInstances.FirstOrDefault(m => m.ServiceName == _selectedServiceFilter)
                : null;

            // Load metric series data with filters
            _metricData = await MetricService.GetMetricSeriesAsync(
                decodedName,
                labelFilters.Count > 0 ? labelFilters : null,
                start,
                end,
                metricForQuery?.Id);

            if (_metricData != null)
            {
                if (!string.IsNullOrEmpty(_selectedServiceFilter))
                {
                    _metricInfo = orderedInstances.FirstOrDefault(m => m.ServiceName == _selectedServiceFilter)
                                  ?? orderedInstances.FirstOrDefault();
                }
                else
                {
                    _metricInfo = orderedInstances.FirstOrDefault();
                }
                _serviceName = _metricInfo?.ServiceName;

                if (_availableServices.Count > 1
                    && string.IsNullOrEmpty(_selectedServiceFilter)
                    && (_metricData.Type == MetricType.GAUGE || _metricData.Type == MetricType.SUM))
                {
                    _multiSeriesData = await MetricService.GetMetricSeriesByServiceAsync(decodedName, start, end);
                }
                else
                {
                    _multiSeriesData = null;
                    _showPerServiceView = false;
                }

                _labels = await MetricService.GetMetricLabelsAsync(decodedName);

                // Detect counter: monotonic cumulative SUM
                var firstPoint = _metricData.Points?.FirstOrDefault();
                _isCounterMetric = _metricData.Type == MetricType.SUM
                    && firstPoint?.IsMonotonic == true
                    && firstPoint.AggregationTemporality == AggregationTemporality.CUMULATIVE;

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

    private async Task OnServiceFilterChanged(string? service)
    {
        _selectedServiceFilter = service;
        await LoadDataAsync();
    }

    private async Task OnLabelFilterChanged(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _selectedLabelFilters.Remove(key);
        }
        else
        {
            _selectedLabelFilters[key] = value;
        }
        await LoadDataAsync();
    }

    private int GetFilteredInstanceCount()
    {
        var instances = _allInstances.AsEnumerable();
        if (!string.IsNullOrEmpty(_selectedServiceFilter))
        {
            instances = instances.Where(m => m.ServiceName == _selectedServiceFilter);
        }
        return instances.Count();
    }

    private void CalculateStats()
    {
        if (_metricData?.Points == null || !_metricData.Points.Any())
        {
            _canShowStats = false;
            _statsAreRates = false;
            _statsAreHistogram = false;
            return;
        }

        bool isCounter = _isCounterMetric && !_showRaw;
        bool isHistogram = _metricData.Type == MetricType.HISTOGRAM;
        _canShowStats = _metricData.Type == MetricType.GAUGE || isCounter || isHistogram;
        _statsAreRates = isCounter;
        _statsAreHistogram = isHistogram;

        if (_canShowStats)
        {
            if (isHistogram)
            {
                // Use the latest point that has Count populated
                var latest = _metricData.Points
                    .OrderByDescending(p => p.Timestamp)
                    .FirstOrDefault(p => p.Count is > 0);

                if (latest != null)
                {
                    _currentValue = (latest.Sum.HasValue && latest.Count > 0)
                        ? latest.Sum!.Value / latest.Count!.Value   // mean
                        : null;
                    _minValue = latest.Min;
                    _maxValue = latest.Max;
                    _avgValue = (double?)latest.Count;
                }
                else
                {
                    _currentValue = _minValue = _maxValue = _avgValue = null;
                }
            }
            else if (isCounter)
            {
                // Compute rate series (Δvalue / Δseconds) and derive stats from rates
                var rates = new List<double>();
                var pts = _metricData.Points;
                for (int i = 1; i < pts.Count; i++)
                {
                    double prevVal = (double)(pts[i - 1].DoubleValue ?? pts[i - 1].IntValue ?? 0);
                    double currVal = (double)(pts[i].DoubleValue ?? pts[i].IntValue ?? 0);
                    if (currVal < prevVal) continue; // counter reset
                    double deltaSeconds = (pts[i].Timestamp - pts[i - 1].Timestamp).TotalSeconds;
                    if (deltaSeconds <= 0) continue;
                    rates.Add((currVal - prevVal) / deltaSeconds);
                }

                if (rates.Any())
                {
                    _currentValue = rates.Last();
                    _minValue = rates.Min();
                    _maxValue = rates.Max();
                    _avgValue = rates.Average();
                }
                else
                {
                    _currentValue = _minValue = _maxValue = _avgValue = null;
                }
            }
            else
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

    private void OnShowRawChanged(bool showRaw)
    {
        _showRaw = showRaw;
        State.ShowRaw = showRaw;
        _ = State.SaveAsync();
        CalculateStats();
    }

    private async Task ExportDataAsync()
    {
        if (_metricData?.Points == null || !_metricData.Points.Any())
            return;

        try
        {
            var csv = new StringBuilder();
            var headers = GetExportHeaders(_metricData.Type);
            csv.AppendLine(string.Join(",", headers));

            foreach (var point in _metricData.Points)
            {
                var row = BuildExportRow(point, _metricData.Type);
                csv.AppendLine(string.Join(",", row.Select(EscapeCsv)));
            }

            var fileName = $"{Uri.UnescapeDataString(MetricName)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}Z.csv";
            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            var base64 = Convert.ToBase64String(bytes);

            await JSRuntime.InvokeVoidAsync("downloadFile", fileName, base64);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error exporting data: {ex.Message}");
        }
    }

    private static string[] GetExportHeaders(MetricType type)
    {
        return type switch
        {
            MetricType.GAUGE => new[] { "Timestamp", "Value", "Attributes" },
            MetricType.SUM => new[] { "Timestamp", "Value", "Attributes" },
            MetricType.HISTOGRAM => new[]
            {
                "Timestamp", "Count", "Sum", "Min", "Max", "BucketCounts", "BucketBounds", "Attributes"
            },
            MetricType.EXPONENTIAL_HISTOGRAM => new[] { "Timestamp", "Count", "Sum", "Min", "Max", "Attributes" },
            MetricType.SUMMARY => new[] { "Timestamp", "Count", "Sum", "Quantiles", "QuantileValues", "Attributes" },
            _ => new[] { "Timestamp", "Value", "Attributes" }
        };
    }

    private static string[] BuildExportRow(MetricDataPoint point, MetricType type)
    {
        var timestamp = point.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var value = (point.DoubleValue ?? point.IntValue)?.ToString() ?? "";
        var attributes = SerializeObject(point.Attributes);

        return type switch
        {
            MetricType.GAUGE => new[] { timestamp, value, attributes },
            MetricType.SUM => new[] { timestamp, value, attributes },
            MetricType.HISTOGRAM => new[]
            {
                timestamp,
                point.Count?.ToString() ?? "",
                point.Sum?.ToString() ?? "",
                point.Min?.ToString() ?? "",
                point.Max?.ToString() ?? "",
                SerializeObject(point.BucketCounts),
                SerializeObject(point.BucketBounds),
                attributes
            },
            MetricType.EXPONENTIAL_HISTOGRAM => new[]
            {
                timestamp,
                point.Count?.ToString() ?? "",
                point.Sum?.ToString() ?? "",
                point.Min?.ToString() ?? "",
                point.Max?.ToString() ?? "",
                attributes
            },
            MetricType.SUMMARY => new[]
            {
                timestamp,
                point.Count?.ToString() ?? "",
                point.Sum?.ToString() ?? "",
                SerializeObject(point.Quantiles),
                SerializeObject(point.QuantileValues),
                attributes
            },
            _ => new[] { timestamp, value, attributes }
        };
    }

    private static string SerializeObject(object? value)
    {
        if (value == null)
            return "";

        return JsonSerializer.Serialize(value);
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
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

        if (Math.Abs(value.Value) >= 1_000_000)
            return $"{value.Value / 1_000_000:F2}M {unit}";
        if (Math.Abs(value.Value) >= 1_000)
            return $"{value.Value / 1_000:F2}K {unit}";

        return $"{value.Value:F2} {unit}";
    }

    private Color GetMetricTypeColor(MetricType type) => type switch
    {
        MetricType.GAUGE => Color.Info,
        MetricType.SUM => Color.Success,
        MetricType.HISTOGRAM => Color.Warning,
        MetricType.EXPONENTIAL_HISTOGRAM => Color.Warning,
        MetricType.SUMMARY => Color.Primary,
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

    private MetricDataPoint? GetLatestPoint()
    {
        return _metricData?.Points
            .OrderByDescending(p => p.Timestamp)
            .FirstOrDefault();
    }

    private List<ExemplarRow> GetExemplarRows()
    {
        if (_metricData?.Points == null || !_metricData.Points.Any())
            return new List<ExemplarRow>();

        return _metricData.Points
            .Where(p => p.Exemplars != null && p.Exemplars.Any())
            .SelectMany(p => p.Exemplars!.Select(ex => new ExemplarRow
            {
                DataPointTimestamp = p.Timestamp,
                ExemplarTimestamp = UnixNanoToDateTime(ex.TimeUnixNano),
                Value = ex.ValueDouble ?? ex.ValueInt,
                TraceIdHex = ex.TraceIdHex,
                SpanIdHex = ex.SpanIdHex,
                FilteredAttributes = ex.FilteredAttributes
            }))
            .OrderByDescending(x => x.ExemplarTimestamp)
            .ToList();
    }

    private static DateTime UnixNanoToDateTime(long unixNano)
        => TimestampConverter.UnixNanoToUtcDateTime(unixNano);

    private sealed class ExemplarRow
    {
        public DateTime DataPointTimestamp { get; set; }
        public DateTime ExemplarTimestamp { get; set; }
        public double? Value { get; set; }
        public string? TraceIdHex { get; set; }
        public string? SpanIdHex { get; set; }
        public Dictionary<string, object>? FilteredAttributes { get; set; }
    }

    public void Dispose()
    {
        TimeRangeState.OnChange -= OnTimeRangeChanged;
        _refreshTimer?.Dispose();
    }
}
