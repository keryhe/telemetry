using ApexCharts;
using Keryhe.Telemetry.Client.Models;
using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Client.Services.State;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Keryhe.Telemetry.Client.Components.Pages;

public partial class Dashboard : ComponentBase, IDisposable
{
    [Inject] private ILogService LogService { get; set; } = null!;
    [Inject] private IMetricService MetricService { get; set; } = null!;
    [Inject] private ITraceService TraceService { get; set; } = null!;
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private DashboardPageState State { get; set; } = null!;
    [Inject] private ServiceMetricsPageState ServiceMetricsState { get; set; } = null!;
    [Inject] private TimeRangeState TimeRangeState { get; set; } = null!;

    private string? _selectedService = null;
    private bool _autoRefresh = false;
    private Timer? _refreshTimer;
    private bool _loading = false;

    private List<string> _availableServices = new();
    private string SelectedServiceValue => _selectedService ?? string.Empty;

    // Stats
    private int _servicesCount = 0;
    private int _totalTraces = 0;
    private double _errorRate = 0;
    private int _errorTraceCount = 0;
    private int _logEventCount = 0;

    // Trace chart
    private record DataPoint(DateTime Time, double Value);
    private List<DataPoint> _traceTotal = new();
    private List<DataPoint> _traceErrors = new();
    private bool _hasTraceData = false;
    private ApexChartOptions<DataPoint> _traceChartOptions = new();

    // Log chart
    private record SeverityPoint(DateTime Time, double Count);
    private Dictionary<string, List<SeverityPoint>> _logSeverityData = new();
    private bool _hasLogData = false;
    private ApexChartOptions<SeverityPoint> _logChartOptions = new();

    // Tables
    private List<TraceInfo> _recentErrorTraces = new();
    private List<TraceInfo> _slowTraces = new();

    // Service strip
    private List<ServiceSummary> _serviceSummaries = new();

    private readonly string[] _severityLevels = { "Trace", "Debug", "Information", "Warning", "Error", "Fatal" };

    private record ServiceSummary(string Name, int TraceCount, int ErrorCount, int MetricCount);

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

    protected override async Task OnInitializedAsync()
    {
        TimeRangeState.OnChange += OnTimeRangeChanged;

        _selectedService = State.SelectedService;
        _autoRefresh = State.AutoRefresh;

        var (init24Start, init24End) = TimeRange.Last24Hours.ToDateTimeRange();
        _availableServices = await TraceService.GetDistinctServicesAsync(init24Start, init24End);

        await LoadDataAsync();

        if (_autoRefresh)
            RestartRefreshTimer();
    }

    private bool _stateLoaded = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _stateLoaded) return;
        _stateLoaded = true;

        await State.LoadAsync();
        _selectedService = State.SelectedService;
        _autoRefresh = State.AutoRefresh;
        StateHasChanged();
    }

    private void OnTimeRangeChanged()
    {
        _ = InvokeAsync(async () =>
        {
            RestartRefreshTimer();
            await LoadDataAsync();
        });
    }

    private async Task LoadDataAsync()
    {
        State.SelectedService = _selectedService;
        State.AutoRefresh = _autoRefresh;
        _ = State.SaveAsync();

        _loading = true;
        StateHasChanged();

        try
        {
            var (start, end) = TimeRangeState.GetDateTimeRange();

            List<TraceInfo> traces;
            if (!string.IsNullOrEmpty(_selectedService))
                traces = await TraceService.GetTracesByServiceAsync(_selectedService, start, end, limit: 500);
            else
                traces = await TraceService.GetTracesByTimeRangeAsync(start, end, limit: 500);

            _totalTraces = traces.Count;
            _errorTraceCount = traces.Count(t => t.HasErrors);
            _errorRate = _totalTraces > 0 ? _errorTraceCount / (double)_totalTraces * 100 : 0;

            _recentErrorTraces = traces
                .Where(t => t.HasErrors)
                .OrderByDescending(t => t.TraceStartTime)
                .Take(10)
                .ToList();

            _slowTraces = traces
                .Where(t => t.TraceDuration > TimeSpan.FromMilliseconds(500))
                .OrderByDescending(t => t.TraceDuration)
                .Take(5)
                .ToList();

            BuildTraceChartData(traces, start, end);

            var logs = await LogService.GetLogRecordsByTimeRangeAsync(start, end);
            if (!string.IsNullOrEmpty(_selectedService))
            {
                logs = logs.Where(l =>
                    l.Resource?.Attributes != null &&
                    l.Resource.Attributes.ContainsKey("service.name") &&
                    l.Resource.Attributes["service.name"]?.ToString() == _selectedService
                ).ToList();
            }
            _logEventCount = logs.Count;
            BuildLogChartData(logs, start, end);

            var services = await TraceService.GetDistinctServicesAsync(start, end);
            _servicesCount = services.Count;
            if (services.Count > 0)
                _availableServices = services;

            var metricSummaries = await MetricService.GetServiceMetricSummariesAsync();
            var metricDict = metricSummaries.ToDictionary(m => m.ServiceName, m => m.MetricCount);

            var tracesByService = traces.GroupBy(t => t.ServiceName ?? "unknown").ToList();
            _serviceSummaries = services.Select(svc =>
            {
                var group = tracesByService.FirstOrDefault(g => g.Key == svc);
                return new ServiceSummary(
                    Name: svc,
                    TraceCount: group?.Count() ?? 0,
                    ErrorCount: group?.Count(t => t.HasErrors) ?? 0,
                    MetricCount: metricDict.TryGetValue(svc, out var mc) ? mc : 0
                );
            }).ToList();
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    private void BuildTraceChartData(List<TraceInfo> traces, DateTime start, DateTime end)
    {
        if (traces.Count == 0)
        {
            _traceTotal = new();
            _traceErrors = new();
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

        foreach (var trace in traces)
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

        _traceChartOptions = new ApexChartOptions<DataPoint>
        {
            Chart = new Chart { Toolbar = new Toolbar { Show = false }, Zoom = new Zoom { Enabled = true, Type = AxisType.X, AllowMouseWheelZoom = false } },
            Colors = new List<string> { "#2196F3", "#F44336" },
            Stroke = new Stroke { Curve = Curve.Straight, Width = new List<int> { 2 } },
            Xaxis = new XAxis { Type = XAxisType.Datetime },
        };

        _hasTraceData = true;
    }

    private void BuildLogChartData(List<LogRecordModel> logs, DateTime start, DateTime end)
    {
        if (logs.Count == 0)
        {
            _logSeverityData = new();
            _hasLogData = false;
            return;
        }

        var duration = end - start;
        var config = SelectBucketConfig(duration);
        var alignedStart = FloorToBucketBoundary(start, config.BucketSize);

        var buckets = new Dictionary<DateTime, Dictionary<string, int>>();
        var current = alignedStart;
        while (current <= end)
        {
            var counts = new Dictionary<string, int>();
            foreach (var sev in _severityLevels) counts[sev] = 0;
            buckets[current] = counts;
            current = current.Add(config.BucketSize);
        }

        foreach (var log in logs.Where(l => l.TimeUnixNano.HasValue))
        {
            var logTime = UnixNanoToDateTime(log.TimeUnixNano!.Value);
            var severity = NormalizeSeverity(log.SeverityText);

            var offset = (long)((logTime - alignedStart).Ticks / config.BucketSize.Ticks);
            var bucketKey = alignedStart + TimeSpan.FromTicks(offset * config.BucketSize.Ticks);

            if (buckets.ContainsKey(bucketKey))
            {
                if (buckets[bucketKey].ContainsKey(severity))
                    buckets[bucketKey][severity]++;
                else
                    buckets[bucketKey]["Debug"]++;
            }
        }

        var sorted = buckets.OrderBy(b => b.Key).ToList();

        _logSeverityData = new Dictionary<string, List<SeverityPoint>>();
        foreach (var sev in _severityLevels)
        {
            _logSeverityData[sev] = sorted
                .Select(b => new SeverityPoint(b.Key, b.Value[sev]))
                .ToList();
        }

        _logChartOptions = new ApexChartOptions<SeverityPoint>
        {
            Chart = new Chart
            {
                Toolbar = new Toolbar { Show = false },
                Stacked = true,
                Zoom = new Zoom { Enabled = true, Type = AxisType.X, AllowMouseWheelZoom = false }
            },
            Colors = new List<string>
            {
                "#9C27B0", // Trace
                "#2196F3", // Debug
                "#4CAF50", // Information
                "#FF9800", // Warning
                "#F44336", // Error
                "#B71C1C"  // Fatal
            },
            Xaxis = new XAxis { Type = XAxisType.Datetime },
        };

        _hasLogData = true;
    }

    private string NormalizeSeverity(string? severityText)
    {
        if (string.IsNullOrEmpty(severityText)) return "Debug";
        foreach (var level in _severityLevels)
        {
            if (string.Equals(severityText, level, StringComparison.OrdinalIgnoreCase))
                return level;
        }
        return "Debug";
    }

    private static DateTime UnixNanoToDateTime(long unixNano)
    {
        long ticks = unixNano / 100;
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return epoch.AddTicks(ticks);
    }

    private async Task OnServiceChanged(string value)
    {
        _selectedService = string.IsNullOrEmpty(value) ? null : value;
        await LoadDataAsync();
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
        _refreshTimer = null;

        if (_autoRefresh)
        {
            var interval = TimeRangeState.SelectedTimeRange.GetRecommendedRefreshInterval();
            _refreshTimer = new Timer(
                async _ => await InvokeAsync(LoadDataAsync),
                null,
                TimeSpan.FromSeconds(interval),
                TimeSpan.FromSeconds(interval)
            );
        }
    }

    private Task HandleChartZoomed(ZoomedData<DataPoint> e)
    {
        if (e.XAxis?.Min == null || e.XAxis?.Max == null) return Task.CompletedTask;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Min)).UtcDateTime;
        var end   = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Max)).UtcDateTime;
        TimeRangeState.SetCustomRange(start, end);
        return Task.CompletedTask;
    }

    private Task HandleLogChartZoomed(ZoomedData<SeverityPoint> e)
    {
        if (e.XAxis?.Min == null || e.XAxis?.Max == null) return Task.CompletedTask;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Min)).UtcDateTime;
        var end   = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Max)).UtcDateTime;
        TimeRangeState.SetCustomRange(start, end);
        return Task.CompletedTask;
    }

    private void NavigateToTrace(string traceId) =>
        NavigationManager.NavigateTo($"/traces?traceId={traceId}");

    private void NavigateToServiceMetrics(string service)
    {
        ServiceMetricsState.SelectedService = service;
        NavigationManager.NavigateTo("/service-metrics");
    }

    private static string ServiceAccentColor(int index) => (index % 6) switch
    {
        0 => "var(--mud-palette-primary)",
        1 => "var(--mud-palette-secondary)",
        2 => "var(--mud-palette-tertiary)",
        3 => "var(--mud-palette-warning)",
        4 => "var(--mud-palette-info)",
        _ => "var(--mud-palette-success)",
        
    };

    private string TruncateId(string id, int maxLength = 16) =>
        id.Length > maxLength ? id[..maxLength] + "..." : id;

    private string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
            return $"{duration.TotalMilliseconds:F0}ms";
        if (duration.TotalMinutes < 1)
            return $"{duration.TotalSeconds:F2}s";
        return $"{duration.TotalMinutes:F2}m";
    }

    public void Dispose()
    {
        TimeRangeState.OnChange -= OnTimeRangeChanged;
        _refreshTimer?.Dispose();
    }
}
