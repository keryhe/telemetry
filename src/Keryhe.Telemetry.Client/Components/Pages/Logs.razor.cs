using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Client.Services.State;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
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
    private List<ChartSeries> _chartSeries = new();
    private string[] _chartXAxisLabels = Array.Empty<string>();
    private string[] _chartColors = Array.Empty<string>();
    private ChartOptions _chartOptions = new();
    // Severity levels in consistent order
    private readonly string[] _severityLevels = new[] { "Trace", "Debug", "Information", "Warning", "Error", "Fatal" };

    // Bucket metadata for chart interactions
    private List<KeyValuePair<DateTime, Dictionary<string, int>>> _sortedBuckets = new();
    private List<int> _bucketFirstLogIndex = new();
    private TimeSpan _currentBucketSize;

    // JS interop
    [Inject] private IJSRuntime JS { get; set; } = null!;
    private ElementReference _chartContainer;
    private double _chartSvgWidth = 0;
    private ElementReference _logGridContainer;
    private const double DenseRowHeightPx = 33.0;

    // Chart margin constants (MudBlazor HorizontalStartSpace minimum / HorizontalEndSpace fixed)
    private const double ChartYAxisWidthPx  = 30.0;
    private const double ChartRightMarginPx = 30.0;

    // Drag/click state
    private bool _isDragging = false;
    private double _pointerDownX = 0;
    private double _dragStartX = 0;
    private double _dragCurrentX = 0;
    private bool _dragThresholdMet = false;
    private int _lastDragEndBucketIdx = -1;
    private const double DragThresholdPx = 5.0;

    // Highlight geometry (pixel values relative to overlay)
    private double _highlightLeft => Math.Min(_dragStartX, _dragCurrentX);
    private double _highlightWidth => Math.Abs(_dragCurrentX - _dragStartX);

    [Inject]
    private ILogService LogService { get; set; } = null!;

    [Inject]
    private LogsPageState State { get; set; } = null!;

    [Inject]
    private TimeRangeState TimeRangeState { get; set; } = null!;

    [SupplyParameterFromQuery(Name = "traceId")]
    public string? TraceIdFilter { get; set; }

    // Computed property to determine if current search is a trace ID search
    // Trace IDs in OpenTelemetry are always exactly 32 hex characters (128 bits)
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

        // Restore filter state
        _selectedService = State.SelectedService;
        _selectedSeverity = State.SelectedSeverity;

        // If traceId is provided in query string, use dedicated trace ID search
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

        // Restore search text when not a traceId query
        _searchText = State.SearchText;

        await SearchLogs();
    }

    private void OnTimeRangeChanged()
    {
        _ = InvokeAsync(SearchLogs);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _chartSvgWidth = await JS.InvokeAsync<double>("getChartSvgWidth", _chartContainer);
        }
    }

    private (int startIdx, int endIdx) PixelRangeToIndexRange(double pixelA, double pixelB)
    {
        if (_sortedBuckets.Count == 0 || _chartSvgWidth <= 0) return (-1, -1);

        double plotLeft  = ChartYAxisWidthPx;
        double plotWidth = _chartSvgWidth - plotLeft - ChartRightMarginPx;
        if (plotWidth <= 0) return (-1, -1);

        int count = _sortedBuckets.Count;
        double bucketPx = plotWidth / count;

        int idxA = (int)Math.Clamp((pixelA - plotLeft) / bucketPx, 0, count - 1);
        int idxB = (int)Math.Clamp((pixelB - plotLeft) / bucketPx, 0, count - 1);

        return (Math.Min(idxA, idxB), Math.Max(idxA, idxB));
    }

    private async Task OnBarClicked(int index)
    {
        if (index < 0 || index >= _bucketFirstLogIndex.Count) return;

        var rowIndex = _bucketFirstLogIndex[index];
        if (rowIndex < 0) return;  // empty bucket

        await JS.InvokeVoidAsync("scrollDataGridToRow", _logGridContainer, rowIndex, DenseRowHeightPx);
    }

    private void OnOverlayPointerDown(PointerEventArgs e)
    {
        if (e.Button != 0) return;
        _pointerDownX = e.OffsetX;
        _dragStartX = e.OffsetX;
        _dragCurrentX = e.OffsetX;
        _dragThresholdMet = false;
        _isDragging = false;
        _lastDragEndBucketIdx = -1;
    }

    private void OnOverlayPointerMove(PointerEventArgs e)
    {
        if (e.Buttons != 1) return;
        _dragCurrentX = e.OffsetX;

        if (!_dragThresholdMet && Math.Abs(_dragCurrentX - _pointerDownX) > DragThresholdPx)
        {
            _dragThresholdMet = true;
            _isDragging = true;
        }

        if (_isDragging)
        {
            var (_, endIdx) = PixelRangeToIndexRange(_dragStartX, _dragCurrentX);
            if (endIdx != _lastDragEndBucketIdx)
            {
                _lastDragEndBucketIdx = endIdx;
                StateHasChanged();
            }
        }
    }

    private void OnOverlayPointerUp(PointerEventArgs e)
    {
        if (!_dragThresholdMet)
        {
            var (clickIdx, _) = PixelRangeToIndexRange(_pointerDownX, _pointerDownX);
            _isDragging = false;
            if (clickIdx >= 0) _ = OnBarClicked(clickIdx);
            return;
        }

        _isDragging = false;
        var (startIdx, endIdx) = PixelRangeToIndexRange(_dragStartX, _dragCurrentX);
        if (startIdx >= 0 && endIdx >= 0)
        {
            var start = _sortedBuckets[startIdx].Key;
            var end = _sortedBuckets[endIdx].Key.Add(_currentBucketSize);
            TimeRangeState.SetCustomRange(start, end);
        }
        StateHasChanged();
    }

    private void OnOverlayPointerCancel(PointerEventArgs e)
    {
        _isDragging = false;
        _dragThresholdMet = false;
        StateHasChanged();
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
        // Persist filter state
        State.SearchText = _searchText;
        State.SelectedService = _selectedService;
        State.SelectedSeverity = _selectedSeverity;

        _dataLoading = true;
        StateHasChanged();

        // Check if searching by trace ID (exactly 32 hex chars per OpenTelemetry spec)
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
            {
                traceLogs = traceLogs.Where(l =>
                    TryGetServiceName(l) == _selectedService
                ).ToList();
            }

            if (!string.IsNullOrEmpty(_selectedSeverity))
            {
                traceLogs = traceLogs.Where(l =>
                    string.Equals(l.SeverityText, _selectedSeverity, StringComparison.OrdinalIgnoreCase)
                ).ToList();
            }

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

        // Normal search: use global time range
        var (start, end) = TimeRangeState.GetDateTimeRange();

        var allLogs = await LogService.GetLogRecordsByTimeRangeAsync(start, end);

        _availableServices = allLogs
            .Select(TryGetServiceName)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .OrderBy(s => s)
            .ToList()!;

        if (!string.IsNullOrEmpty(_selectedService))
        {
            allLogs = allLogs.Where(l =>
                TryGetServiceName(l) == _selectedService
            ).ToList();
        }

        if (!string.IsNullOrEmpty(_selectedSeverity))
        {
            allLogs = allLogs.Where(l =>
                string.Equals(l.SeverityText, _selectedSeverity, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            allLogs = ApplyTextSearch(allLogs, _searchText);
        }

        _logs = allLogs;
        UpdateChartData(start, end);
        _dataLoading = false;
        _chartSvgWidth = await JS.InvokeAsync<double>("getChartSvgWidth", _chartContainer);
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
            {
                logs = ApplyAttributeFilter(logs, cleanTerm);
            }
            else
            {
                logs = logs.Where(l =>
                    l.BodyValue?.Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ?? false
                ).ToList();
            }
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

    private record BucketConfig(
        TimeSpan BucketSize,
        string LabelFormat,
        Func<DateTime, bool> ShowLabel
    );

    private static readonly BucketConfig[] BucketConfigs =
    [
        new(TimeSpan.FromMinutes(1),  "HH:mm",  dt => dt.Minute % 15 == 0),
        new(TimeSpan.FromMinutes(5),  "HH:mm",  dt => dt.Minute == 0),
        new(TimeSpan.FromMinutes(10), "HH:mm",  dt => dt.Minute == 0),
        new(TimeSpan.FromMinutes(30), "HH:mm",  dt => dt.Hour % 6 == 0 && dt.Minute == 0),
        new(TimeSpan.FromHours(1),    "HH:mm",  dt => dt.Hour % 6 == 0 && dt.Minute == 0),
        new(TimeSpan.FromHours(3),    "MM/dd",  dt => dt.Hour == 0),
        new(TimeSpan.FromHours(6),    "MM/dd",  dt => dt.Hour == 0),
        new(TimeSpan.FromHours(12),   "MM/dd",  dt => dt.Day % 2 == 1 && dt.Hour == 0),
        new(TimeSpan.FromDays(1),     "MM/dd",  dt => dt.Day % 7 == 1 || dt.Day == 1),
        new(TimeSpan.FromDays(2),     "MM/dd",  dt => dt.Day <= 2 || (dt.Day >= 15 && dt.Day <= 16)),
        new(TimeSpan.FromDays(7),     "MMM",    dt => dt.Day <= 7),
        new(TimeSpan.FromDays(30),    "MMM yy", dt => true),
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
        start = start.ToLocalTime();
        end   = end.ToLocalTime();

        if (_logs.Count == 0)
        {
            _chartSeries = new List<ChartSeries>();
            _chartXAxisLabels = Array.Empty<string>();
            _chartColors = Array.Empty<string>();
            _chartOptions = new ChartOptions { YAxisTicks = 10 };
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
            {
                severityCounts[severity] = 0;
            }
            buckets[currentBucket] = severityCounts;
            currentBucket = currentBucket.Add(config.BucketSize);
        }

        var firstLogInBucket = new Dictionary<DateTime, int>();

        for (int logIdx = 0; logIdx < _logs.Count; logIdx++)
        {
            var log = _logs[logIdx];
            if (!log.TimeUnixNano.HasValue) continue;

            var logTime = UnixNanoToDateTime(log.TimeUnixNano.Value);
            var severity = log.SeverityText ?? "Debug";

            // O(1) bucket lookup via integer division
            var offset = (long)((logTime - alignedStart).Ticks / config.BucketSize.Ticks);
            var bucketKey = alignedStart + TimeSpan.FromTicks(offset * config.BucketSize.Ticks);

            if (buckets.ContainsKey(bucketKey))
            {
                if (buckets[bucketKey].ContainsKey(severity))
                    buckets[bucketKey][severity]++;
                else
                    buckets[bucketKey]["Debug"]++;

                if (!firstLogInBucket.ContainsKey(bucketKey))
                    firstLogInBucket[bucketKey] = logIdx;
            }
        }

        var sortedBuckets = buckets.OrderBy(b => b.Key).ToList();
        _sortedBuckets = sortedBuckets;
        _bucketFirstLogIndex = sortedBuckets
            .Select(b => firstLogInBucket.GetValueOrDefault(b.Key, -1))
            .ToList();
        _currentBucketSize = config.BucketSize;

        _chartXAxisLabels = sortedBuckets.Select(b =>
            config.ShowLabel(b.Key) ? b.Key.ToString(config.LabelFormat) : ""
        ).ToArray();

        _chartSeries = new List<ChartSeries>();
        _chartColors = new string[_severityLevels.Length];

        for (int i = 0; i < _severityLevels.Length; i++)
        {
            var severity = _severityLevels[i];
            var chartData = sortedBuckets.Select(b => (double)b.Value[severity]).ToArray();

            _chartSeries.Add(new ChartSeries
            {
                Name = severity,
                Data = chartData
            });

            _chartColors[i] = GetSeverityColor(severity);
        }

        var maxValue = sortedBuckets.Max(b => b.Value.Values.Sum());
        int yAxisTicks;

        if (maxValue < 10) yAxisTicks = 1;
        else if (maxValue < 50) yAxisTicks = 5;
        else if (maxValue < 100) yAxisTicks = 10;
        else if (maxValue < 500) yAxisTicks = 50;
        else if (maxValue < 1000) yAxisTicks = 100;
        else if (maxValue < 5000) yAxisTicks = 500;
        else if (maxValue < 10000) yAxisTicks = 1000;
        else if (maxValue < 50000) yAxisTicks = 5000;
        else if (maxValue < 100000) yAxisTicks = 10000;
        else yAxisTicks = 50000;

        _chartOptions = new ChartOptions
        {
            YAxisTicks = yAxisTicks,
            ChartPalette = _chartColors
        };
    }

    private DateTime UnixNanoToDateTime(long unixNano)
    {
        long ticks = unixNano / 100;
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return epoch.AddTicks(ticks).ToLocalTime();
    }

    public void Dispose()
    {
        TimeRangeState.OnChange -= OnTimeRangeChanged;
    }
}
