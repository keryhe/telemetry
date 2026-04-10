using ApexCharts;
using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Client.Services.State;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Keryhe.Telemetry.Client.Components.Pages;

public partial class Logs : ComponentBase, IDisposable
{
    private List<LogRecordModel> _logs = new();
    private bool _dataLoading = true;
    private string _searchText = "";

    // Filter dropdowns
    private string? _selectedService = null;
    private string? _selectedSeverity = null;
    private List<string> _availableServices = new();

    // Search help dialog
    private bool _showSearchHelp = false;

    // Chart data
    private record SeverityPoint(DateTime Time, double Count);
    private Dictionary<string, List<SeverityPoint>> _apexSeverityData = new();
    private int _chartRenderKey = 0;
    private ApexChartOptions<SeverityPoint> _chartOptions = new()
    {
        Chart = new Chart
        {
            Toolbar = new Toolbar { Show = false },
            Stacked = true,
            Zoom = new Zoom { Enabled = true, Type = AxisType.X }
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
        Legend = new Legend { Position = LegendPosition.Right },
    };

    // Severity levels in consistent order
    private readonly string[] _severityLevels = new[] { "Trace", "Debug", "Information", "Warning", "Error", "Fatal" };

    [Inject]
    private ILogService LogService { get; set; } = null!;

    [Inject]
    private LogsPageState State { get; set; } = null!;

    [Inject]
    private TimeRangeState TimeRangeState { get; set; } = null!;

    [SupplyParameterFromQuery(Name = "traceId")]
    public string? TraceIdFilter { get; set; }

    private bool IsTraceIdSearch => !string.IsNullOrWhiteSpace(_searchText) &&
                                     _searchText.Length == 32 &&
                                     !_searchText.Contains(' ') &&
                                     !_searchText.Contains('=') &&
                                     !_searchText.Contains(':') &&
                                     !_searchText.Contains('"');

    protected override async Task OnInitializedAsync()
    {
        TimeRangeState.OnChange += OnTimeRangeChanged;
        _dataLoading = true;

        _selectedService = State.SelectedService;
        _selectedSeverity = State.SelectedSeverity;

        if (!string.IsNullOrEmpty(TraceIdFilter))
        {
            _searchText = TraceIdFilter;

            var traceLogs = await LogService.GetLogRecordsByTraceIdAsync(TraceIdFilter);

            _availableServices = traceLogs
                .Select(TryGetServiceName)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .OrderBy(s => s)
                .ToList()!;

            _logs = traceLogs;

            if (_logs.Any() && _logs.Any(l => l.TimeUnixNano.HasValue))
            {
                var minTime = _logs.Where(l => l.TimeUnixNano.HasValue)
                    .Min(l => UnixNanoToDateTime(l.TimeUnixNano!.Value));
                var maxTime = _logs.Where(l => l.TimeUnixNano.HasValue)
                    .Max(l => UnixNanoToDateTime(l.TimeUnixNano!.Value));
                UpdateChartData(minTime, maxTime.AddDays(1));
            }

            _dataLoading = false;
            return;
        }

        _searchText = State.SearchText;

        await SearchLogs();
    }

    private void OnTimeRangeChanged()
    {
        _ = InvokeAsync(SearchLogs);
    }

    private Task HandleChartZoomed(ZoomedData<SeverityPoint> e)
    {
        if (e.XAxis?.Min == null || e.XAxis?.Max == null) return Task.CompletedTask;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Min)).UtcDateTime;
        var end   = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(e.XAxis.Max)).UtcDateTime;
        TimeRangeState.SetCustomRange(start, end);
        return Task.CompletedTask;
    }

    private string FormatUnixNanoToDateTime(long unixNano)
    {
        long ticks = unixNano / 100;
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTime = epoch.AddTicks(ticks);
        return dateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
    }

    private string GetSeverityColor(string? severity)
    {
        return severity?.ToUpper() switch
        {
            "TRACE" => Colors.Purple.Default,
            "DEBUG" => Colors.Blue.Default,
            "INFORMATION" => Colors.Green.Default,
            "WARNING" => Colors.Orange.Default,
            "ERROR" => Colors.Red.Default,
            "FATAL" => Colors.Red.Darken4,
            _ => Colors.Gray.Default
        };
    }

    private string GetSeverityLightColor(string? severity)
    {
        return severity?.ToUpper() switch
        {
            "TRACE"       => "var(--severity-trace-bg)",
            "DEBUG"       => "var(--severity-debug-bg)",
            "INFORMATION" => "var(--severity-info-bg)",
            "WARNING"     => "var(--severity-warning-bg)",
            "ERROR"       => "var(--severity-error-bg)",
            "FATAL"       => "var(--severity-fatal-bg)",
            _             => "transparent"
        };
    }

    private string GetLevelCellStyle(string? severity)
    {
        if (severity?.ToUpper() == "INFORMATION")
            return $"background-color: var(--mud-palette-surface); text-align: center; color: {GetSeverityColor(severity)};";

        return $"background-color: {GetSeverityLightColor(severity)}; text-align: center; color: {GetSeverityColor(severity)};";
    }

    private static string? TryGetServiceName(LogRecordModel log)
    {
        if (log.Resource?.Attributes == null)
            return null;

        return log.Resource.Attributes.TryGetValue("service.name", out var serviceName)
            ? serviceName?.ToString()
            : null;
    }

    private async Task SearchLogs()
    {
        State.SearchText = _searchText;
        State.SelectedService = _selectedService;
        State.SelectedSeverity = _selectedSeverity;

        _dataLoading = true;
        StateHasChanged();

        try
        {
            bool isTraceIdSearch = !string.IsNullOrWhiteSpace(_searchText) &&
                                   _searchText.Length == 32 &&
                                   !_searchText.Contains(' ') &&
                                   !_searchText.Contains('=') &&
                                   !_searchText.Contains(':') &&
                                   !_searchText.Contains('"');

            if (isTraceIdSearch)
            {
                var traceLogs = await LogService.GetLogRecordsByTraceIdAsync(_searchText);

                if (!string.IsNullOrEmpty(_selectedService))
                    traceLogs = traceLogs.Where(l => TryGetServiceName(l) == _selectedService).ToList();

                if (!string.IsNullOrEmpty(_selectedSeverity))
                    traceLogs = traceLogs.Where(l =>
                        string.Equals(l.SeverityText, _selectedSeverity, StringComparison.OrdinalIgnoreCase)
                    ).ToList();

                _logs = traceLogs;

                if (_logs.Any() && _logs.Any(l => l.TimeUnixNano.HasValue))
                {
                    var minTime = _logs.Where(l => l.TimeUnixNano.HasValue)
                        .Min(l => UnixNanoToDateTime(l.TimeUnixNano!.Value));
                    var maxTime = _logs.Where(l => l.TimeUnixNano.HasValue)
                        .Max(l => UnixNanoToDateTime(l.TimeUnixNano!.Value));
                    UpdateChartData(minTime, maxTime.AddDays(1));
                }

                return;
            }

            var (start, end) = TimeRangeState.GetDateTimeRange();

            var allLogs = await LogService.GetLogRecordsByTimeRangeAsync(start, end);

            _availableServices = allLogs
                .Select(TryGetServiceName)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .OrderBy(s => s)
                .ToList()!;

            if (!string.IsNullOrEmpty(_selectedService))
                allLogs = allLogs.Where(l => TryGetServiceName(l) == _selectedService).ToList();

            if (!string.IsNullOrEmpty(_selectedSeverity))
                allLogs = allLogs.Where(l =>
                    string.Equals(l.SeverityText, _selectedSeverity, StringComparison.OrdinalIgnoreCase)
                ).ToList();

            if (!string.IsNullOrWhiteSpace(_searchText))
                allLogs = ApplyTextSearch(allLogs, _searchText);

            _logs = allLogs;
            UpdateChartData(start, end);
        }
        finally
        {
            _dataLoading = false;
            StateHasChanged();
        }
    }

    private List<LogRecordModel> ApplyTextSearch(List<LogRecordModel> logs, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return logs;

        var terms = searchText.Split(new[] { " AND " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var term in terms)
        {
            var cleanTerm = term.Trim();

            if (cleanTerm.Contains('=') || cleanTerm.Contains(':'))
                logs = ApplyAttributeFilter(logs, cleanTerm);
            else
                logs = logs.Where(l =>
                    l.BodyValue?.Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ?? false
                ).ToList();
        }

        return logs;
    }

    private List<LogRecordModel> ApplyAttributeFilter(List<LogRecordModel> logs, string filter)
    {
        var separator = filter.Contains('=') ? '=' : ':';
        var parts = filter.Split(separator, 2);

        if (parts.Length != 2) return logs;

        var key = parts[0].Trim();
        var value = parts[1].Trim().Trim('"', '\'');
        var isExactMatch = separator == '=';

        return logs.Where(l =>
        {
            if (l.Attributes?.ContainsKey(key) == true)
            {
                var attrValue = l.Attributes[key]?.ToString() ?? "";
                return isExactMatch
                    ? attrValue.Equals(value, StringComparison.OrdinalIgnoreCase)
                    : attrValue.Contains(value, StringComparison.OrdinalIgnoreCase);
            }

            if (l.Resource?.Attributes?.ContainsKey(key) == true)
            {
                var attrValue = l.Resource.Attributes[key]?.ToString() ?? "";
                return isExactMatch
                    ? attrValue.Equals(value, StringComparison.OrdinalIgnoreCase)
                    : attrValue.Contains(value, StringComparison.OrdinalIgnoreCase);
            }

            if (l.InstrumentationScope?.Attributes?.ContainsKey(key) == true)
            {
                var attrValue = l.InstrumentationScope.Attributes[key]?.ToString() ?? "";
                return isExactMatch
                    ? attrValue.Equals(value, StringComparison.OrdinalIgnoreCase)
                    : attrValue.Contains(value, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }).ToList();
    }

    private string FormatAttributeValue(object? value)
    {
        if (value == null) return "null";
        if (value is string s) return s;
        if (value is bool b) return b.ToString().ToLower();
        if (value is int || value is long || value is double || value is decimal) return value.ToString()!;
        if (value is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
        return value.ToString() ?? "N/A";
    }

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

    private const int MaxBucketCount = 100;

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

        if (_logs.Count == 0)
        {
            _apexSeverityData = new Dictionary<string, List<SeverityPoint>>();
            _chartRenderKey++;
            return;
        }

        var duration = end - start;
        var config = SelectBucketConfig(duration);
        var alignedStart = FloorToBucketBoundary(start, config.BucketSize);

        var buckets = new Dictionary<DateTime, Dictionary<string, int>>();
        var currentBucket = alignedStart;

        while (currentBucket <= end)
        {
            var severityCounts = new Dictionary<string, int>();
            foreach (var severity in _severityLevels)
                severityCounts[severity] = 0;
            buckets[currentBucket] = severityCounts;
            currentBucket = currentBucket.Add(config.BucketSize);
        }

        for (int logIdx = 0; logIdx < _logs.Count; logIdx++)
        {
            var log = _logs[logIdx];
            if (!log.TimeUnixNano.HasValue) continue;

            var logTime = UnixNanoToDateTime(log.TimeUnixNano.Value);
            var severity = log.SeverityText ?? "Debug";

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

        var sortedBuckets = buckets.OrderBy(b => b.Key).ToList();

        _apexSeverityData = new Dictionary<string, List<SeverityPoint>>();
        foreach (var severity in _severityLevels)
        {
            _apexSeverityData[severity] = sortedBuckets
                .Select(b => new SeverityPoint(b.Key, b.Value[severity]))
                .ToList();
        }
        _chartRenderKey++;
    }

    private DateTime UnixNanoToDateTime(long unixNano)
    {
        long ticks = unixNano / 100;
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return epoch.AddTicks(ticks);
    }

    public void Dispose()
    {
        TimeRangeState.OnChange -= OnTimeRangeChanged;
    }
}
