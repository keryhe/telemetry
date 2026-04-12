using ApexCharts;
using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Client.Services.State;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Color = MudBlazor.Color;
using Size = MudBlazor.Size;

namespace Keryhe.Telemetry.Client.Components.Pages;

public partial class Traces : ComponentBase, IDisposable
{
    private bool _stateLoaded = false;
    private int _activeTabIndexBacking = 0;
    private int _activeTabIndex
    {
        get => _activeTabIndexBacking;
        set { _activeTabIndexBacking = value; State.ActiveTabIndex = value; _ = State.SaveAsync(); }
    }
    private List<TraceInfo> _traces = new();
    private List<ServiceDependency> _serviceDependencies = new();

    private Dictionary<string, int> _operationCounts = new();
    private Dictionary<string, double> _averageLatencies = new();
    private List<string> _availableServices = new();
    private bool _dataLoading = true;
    private string _searchText = "";
    private bool _showSearchHelp = false;
    private string _filterMode = "all";

    private bool IsTraceIdSearch => SearchQueryParser.Parse(_searchText).IsTraceIdSearch;
    private string? _selectedService = null;
    private int _minDurationMs = 500;

    private string? _selectedAnalyticsService = null;

    // Stats cards
    private int _totalTraces = 0;
    private double _avgDuration = 0;
    private double _errorRate = 0;
    private int _servicesCount = 0;

    // Chart data
    private record DataPoint(DateTime Time, double Value);
    private List<DataPoint> _traceTotal = new();
    private List<DataPoint> _traceErrors = new();
    private ApexChartOptions<DataPoint> _chartOptions = new();
    private bool _hasTraceData = false;

    [Inject]
    private ITraceService TraceService { get; set; } = null!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    [Inject]
    private TracesPageState State { get; set; } = null!;

    [Inject]
    private TimeRangeState TimeRangeState { get; set; } = null!;

    protected override async Task OnInitializedAsync()
    {
        TimeRangeState.OnChange += OnTimeRangeChanged;

        _filterMode = State.FilterMode;
        _selectedService = State.SelectedService;
        _minDurationMs = State.MinDurationMs;
        _selectedAnalyticsService = State.SelectedAnalyticsService;
        _searchText = State.SearchText;

        _activeTabIndexBacking = Math.Min(State.ActiveTabIndex, 2);

        await LoadDataAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _stateLoaded) return;
        _stateLoaded = true;

        await State.LoadAsync();
        _searchText = State.SearchText;
        _selectedService = State.SelectedService;
        _filterMode = State.FilterMode;
        _minDurationMs = State.MinDurationMs;
        _selectedAnalyticsService = State.SelectedAnalyticsService;
        _activeTabIndexBacking = Math.Min(State.ActiveTabIndex, 2);
        StateHasChanged();
    }

    private void OnTimeRangeChanged()
    {
        _ = InvokeAsync(LoadDataAsync);
    }

    private async Task LoadDataAsync()
    {
        State.FilterMode = _filterMode;
        State.SelectedService = _selectedService;
        State.MinDurationMs = _minDurationMs;
        State.SearchText = _searchText;
        _ = State.SaveAsync();

        _dataLoading = true;
        StateHasChanged();

        try
        {
            var (start, end) = TimeRangeState.GetDateTimeRange();

            switch (_filterMode)
            {
                case "errors":
                    _traces = await TraceService.GetErrorTracesAsync(start, end, limit: 200);
                    break;
                case "slow":
                    _traces = await TraceService.GetSlowTracesAsync(TimeSpan.FromMilliseconds(_minDurationMs), start, end, limit: 200);
                    break;
                default:
                    if (!string.IsNullOrEmpty(_selectedService))
                        _traces = await TraceService.GetTracesByServiceAsync(_selectedService, start, end, limit: 200);
                    else
                        _traces = await TraceService.GetTracesByTimeRangeAsync(start, end, limit: 200);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                var parsedQuery = SearchQueryParser.Parse(_searchText);

                if (parsedQuery.IsTraceIdSearch)
                {
                    _traces = _traces.Where(t =>
                        t.TraceIdHex.Equals(parsedQuery.TraceId, StringComparison.OrdinalIgnoreCase)
                    ).ToList();
                }
                else if (parsedQuery.Terms.Count > 0)
                {
                    _traces = ApplyParsedSearch(_traces, parsedQuery);
                }
            }

            CalculateStats();
            UpdateChartData(start, end);

            _serviceDependencies = await TraceService.GetServiceDependenciesAsync(start, end);
            _availableServices = await TraceService.GetDistinctServicesAsync(start, end);
        }
        finally
        {
            _dataLoading = false;
            StateHasChanged();
        }
    }

    private async Task SearchTraces() => await LoadDataAsync();

    private async Task OnFilterModeChanged(string mode)
    {
        _filterMode = mode;
        await LoadDataAsync();
    }

    private async Task OnServiceFilterChanged(string? service)
    {
        _selectedService = service;
        await LoadDataAsync();
    }

    private void ViewTraceDetails(string traceId)
    {
        NavigationManager.NavigateTo($"/traces/{traceId}");
    }

    private async Task OnAnalyticsServiceChanged(string? service)
    {
        _selectedAnalyticsService = service;
        State.SelectedAnalyticsService = service;
        _ = State.SaveAsync();

        if (string.IsNullOrEmpty(service))
        {
            _operationCounts.Clear();
            _averageLatencies.Clear();
            return;
        }

        _dataLoading = true;
        StateHasChanged();

        try
        {
            var (start, end) = TimeRangeState.GetDateTimeRange();
            _operationCounts = await TraceService.GetOperationCountsAsync(service, start, end);
            _averageLatencies = await TraceService.GetAverageLatenciesAsync(service, start, end);
        }
        finally
        {
            _dataLoading = false;
            StateHasChanged();
        }
    }

    private void CalculateStats()
    {
        _totalTraces = _traces.Count;
        _avgDuration = _traces.Any() ? _traces.Average(t => t.TraceDuration.TotalMilliseconds) : 0;
        _errorRate = _traces.Any() ? (_traces.Count(t => t.HasErrors) / (double)_traces.Count) * 100 : 0;
        _servicesCount = _traces.Select(t => t.ServiceName).Distinct().Count();
    }

    // ── Trace Volume chart ────────────────────────────────────────────────────

    private record BucketConfig(TimeSpan BucketSize, string LabelFormat);

    private static readonly BucketConfig[] BucketConfigs =
    [
        new(TimeSpan.FromMinutes(1),  "HH:mm"),
        new(TimeSpan.FromMinutes(5),  "HH:mm"),
        new(TimeSpan.FromMinutes(10), "HH:mm"),
        new(TimeSpan.FromMinutes(30), "HH:mm"),
        new(TimeSpan.FromHours(1),    "HH:mm"),
        new(TimeSpan.FromHours(3),    "MM/dd"),
        new(TimeSpan.FromHours(6),    "MM/dd"),
        new(TimeSpan.FromHours(12),   "MM/dd"),
        new(TimeSpan.FromDays(1),     "MM/dd"),
        new(TimeSpan.FromDays(2),     "MM/dd"),
        new(TimeSpan.FromDays(7),     "MMM"),
        new(TimeSpan.FromDays(30),    "MMM yy"),
    ];

    private const int MaxBucketCount = 50;

    private static BucketConfig SelectBucketConfig(TimeSpan duration)
    {
        foreach (var config in BucketConfigs)
        {
            if ((long)(duration / config.BucketSize) <= MaxBucketCount)
                return config;
        }
        return BucketConfigs[^1];
    }

    private static DateTime FloorToBucketBoundary(DateTime dt, TimeSpan bucketSize)
    {
        if (bucketSize >= TimeSpan.FromDays(1))
            return new DateTime(dt.Year, dt.Month, dt.Day, 0, 0, 0, dt.Kind);
        return new DateTime(dt.Ticks / bucketSize.Ticks * bucketSize.Ticks, dt.Kind);
    }

    private void UpdateChartData(DateTime start, DateTime end)
    {
        if (_traces.Count == 0)
        {
            _traceTotal = new();
            _traceErrors = new();
            _chartOptions = new ApexChartOptions<DataPoint>();
            _hasTraceData = false;
            return;
        }

        var duration = end - start;
        var config = SelectBucketConfig(duration);
        var alignedStart = FloorToBucketBoundary(start, config.BucketSize);

        var buckets = new Dictionary<DateTime, (int Total, int Errors)>();
        var current = alignedStart;
        while (current <= end)
        {
            buckets[current] = (0, 0);
            current = current.Add(config.BucketSize);
        }

        foreach (var trace in _traces)
        {
            var offset = (long)((trace.TraceStartTime - alignedStart).Ticks / config.BucketSize.Ticks);
            var bucketKey = alignedStart + TimeSpan.FromTicks(offset * config.BucketSize.Ticks);
            if (buckets.ContainsKey(bucketKey))
            {
                var cur = buckets[bucketKey];
                buckets[bucketKey] = (cur.Total + 1, cur.Errors + (trace.HasErrors ? 1 : 0));
            }
        }

        var sorted = buckets.OrderBy(b => b.Key).ToList();
        _traceTotal  = sorted.Select(b => new DataPoint(b.Key, b.Value.Total)).ToList();
        _traceErrors = sorted.Select(b => new DataPoint(b.Key, b.Value.Errors)).ToList();

        _chartOptions = new ApexChartOptions<DataPoint>
        {
            Chart = new Chart { Toolbar = new Toolbar { Show = false }, Zoom = new Zoom { Enabled = true, Type = AxisType.X } },
            Colors = new List<string> { "#2196F3", "#F44336" },
            Stroke = new Stroke { Curve = Curve.Straight, Width = new List<int> { 2 } },
            Xaxis = new XAxis
            {
                Type = XAxisType.Datetime,
                Labels = new XAxisLabels
                {
                    DatetimeUTC = false,
                    DatetimeFormatter = new DatetimeFormatter
                    {
                        Year   = "yyyy",
                        Month  = "MMM yy",
                        Day    = config.LabelFormat,
                        Hour   = config.LabelFormat,
                        Minute = config.LabelFormat,
                    }
                }
            },
        };

        _hasTraceData = true;
    }

    private Task HandleChartZoomed(ZoomedData<DataPoint> e)
    {
        if (e.XAxis?.Min == null || e.XAxis?.Max == null) return Task.CompletedTask;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Min)).UtcDateTime;
        var end   = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Max)).UtcDateTime;
        TimeRangeState.SetCustomRange(start, end);
        return Task.CompletedTask;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)  return $"{duration.TotalMilliseconds:F0}ms";
        if (duration.TotalMinutes < 1)  return $"{duration.TotalSeconds:F2}s";
        return $"{duration.TotalMinutes:F2}m";
    }

    private string FormatDateTime(DateTime dateTime) =>
        dateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");

    private string GetDurationColor(double milliseconds)
    {
        if (milliseconds < 100)  return Colors.Green.Default;
        if (milliseconds < 500)  return Colors.Yellow.Darken2;
        return Colors.Red.Default;
    }

    private string GetStatusColor(bool hasErrors) =>
        hasErrors ? Colors.Red.Default : Colors.Green.Default;

    private string GetStatusIcon(bool hasErrors) =>
        hasErrors ? Icons.Material.Filled.Error : Icons.Material.Filled.CheckCircle;

    private string GetSpanKindColor(SpanKind kind) => kind switch
    {
        SpanKind.CLIENT   => Colors.Blue.Default,
        SpanKind.SERVER   => Colors.Green.Default,
        SpanKind.INTERNAL => Colors.Gray.Default,
        SpanKind.PRODUCER => Colors.Purple.Default,
        SpanKind.CONSUMER => Colors.Orange.Default,
        _                 => Colors.Gray.Default
    };

    private string TruncateTraceId(string traceId, int maxLength = 16) =>
        traceId.Length > maxLength ? traceId[..maxLength] + "..." : traceId;

    private List<TraceInfo> ApplyParsedSearch(List<TraceInfo> traces, ParsedSearchQuery query)
    {
        foreach (var term in query.Terms)
        {
            if (term.IsAttributeFilter)
            {
                var key = term.Key!;
                var value = term.Value!;
                var exactMatch = term.IsExactMatch;

                traces = traces.Where(t =>
                {
                    if (t.RootSpanAttributes?.ContainsKey(key) == true)
                    {
                        var v = t.RootSpanAttributes[key]?.ToString() ?? "";
                        return exactMatch
                            ? v.Equals(value, StringComparison.OrdinalIgnoreCase)
                            : v.Contains(value, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                }).ToList();
            }
            else
            {
                var text = term.FreeText!;
                traces = traces.Where(t =>
                    (t.RootOperationName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (t.ServiceName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                ).ToList();
            }
        }

        return traces;
    }

    public void Dispose()
    {
        TimeRangeState.OnChange -= OnTimeRangeChanged;
    }
}
