using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Keryhe.Telemetry.Client.Components.Pages;

public partial class Logs : ComponentBase
{
    private List<LogRecordModel> _logs = new();
    private bool _dataLoading = true;
    private LogRecordModel? _expandedItem;
    private string _searchText = "";
    private DateRange? _dateRange = new DateRange(DateTime.Today.AddDays(-6), DateTime.Today);
    
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
    private ChartOptions _chartOptions = new() ;
    // Severity levels in consistent order
    private readonly string[] _severityLevels = new[] { "Trace", "Debug", "Information", "Warning", "Error", "Fatal" };
    
    [Inject]
    private ILogService LogService { get; set; }

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
        _dataLoading = true;
        
        // If traceId is provided in query string, use dedicated trace ID search
        if (!string.IsNullOrEmpty(TraceIdFilter))
        {
            _searchText = TraceIdFilter;
            
            // Use dedicated method that doesn't require date range
            var traceLogs = await LogService.GetLogRecordsByTraceIdAsync(TraceIdFilter);
            
            // Extract available services from logs
            _availableServices = traceLogs
                .Where(l => l.Resource?.Attributes != null && l.Resource.Attributes.ContainsKey("service.name"))
                .Select(l => l.Resource.Attributes["service.name"]?.ToString())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .OrderBy(s => s)
                .ToList()!;
            
            _logs = traceLogs;
            
            // Set date range to null to indicate it's not being used
            _dateRange = null;
            
            // Update chart with logs' actual time range
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
        
        // Normal flow: require date range for time-based search
        if (_dateRange?.Start == null || _dateRange?.End == null)
        {
            _dataLoading = false;
            return;
        }
        var start = _dateRange.Start.Value;
        var end = _dateRange.End.Value.AddDays(1);
        
        // Load all logs first
        var allLogs = await LogService.GetLogRecordsByTimeRangeAsync(start, end);
        
        // Extract available services from logs
        _availableServices = allLogs
            .Where(l => l.Resource?.Attributes != null && l.Resource.Attributes.ContainsKey("service.name"))
            .Select(l => l.Resource.Attributes["service.name"]?.ToString())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .OrderBy(s => s)
            .ToList()!;
        
        // Apply search filter if traceId is provided
        if (!string.IsNullOrEmpty(_searchText))
        {
            allLogs = allLogs.Where(l => 
                l.TraceIdHex?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) == true
            ).ToList();
        }
        
        _logs = allLogs;
        UpdateChartData(start, end);
        _dataLoading = false;
    }

    private string FormatUnixNanoToDateTime(long unixNano)
    {
        // Convert nanoseconds to ticks (1 tick = 100 nanoseconds)
        long ticks = unixNano / 100;
        
        // Unix epoch start: January 1, 1970
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTime = epoch.AddTicks(ticks);
        
        // Format as local time
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
    
    private async Task SearchLogs()
    {
        _dataLoading = true;
        StateHasChanged();

        // Check if searching by trace ID (exactly 32 hex chars per OpenTelemetry spec)
        bool isTraceIdSearch = !string.IsNullOrWhiteSpace(_searchText) && 
                               _searchText.Length == 32 && 
                               !_searchText.Contains(' ') &&
                               !_searchText.Contains('=') &&
                               !_searchText.Contains(':') &&
                               !_searchText.Contains('"');

        // If searching by trace ID, use dedicated method
        if (isTraceIdSearch)
        {
            var traceLogs = await LogService.GetLogRecordsByTraceIdAsync(_searchText);
            
            // Apply service filter if selected
            if (!string.IsNullOrEmpty(_selectedService))
            {
                traceLogs = traceLogs.Where(l => 
                    l.Resource?.Attributes != null &&
                    l.Resource.Attributes.ContainsKey("service.name") &&
                    l.Resource.Attributes["service.name"]?.ToString() == _selectedService
                ).ToList();
            }
            
            // Apply severity filter if selected
            if (!string.IsNullOrEmpty(_selectedSeverity))
            {
                traceLogs = traceLogs.Where(l => 
                    string.Equals(l.SeverityText, _selectedSeverity, StringComparison.OrdinalIgnoreCase)
                ).ToList();
            }
            
            _logs = traceLogs;
            
            // Update chart with logs' actual time range
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

        // Normal search: require date range
        if (_dateRange?.Start == null || _dateRange?.End == null)
        {
            _dataLoading = false;
            return;
        }
        var start = _dateRange.Start.Value;
        var end = _dateRange.End.Value.AddDays(1);
        
        // Load all logs
        var allLogs = await LogService.GetLogRecordsByTimeRangeAsync(start, end);
        
        // Apply service filter
        if (!string.IsNullOrEmpty(_selectedService))
        {
            allLogs = allLogs.Where(l => 
                l.Resource?.Attributes != null &&
                l.Resource.Attributes.ContainsKey("service.name") &&
                l.Resource.Attributes["service.name"]?.ToString() == _selectedService
            ).ToList();
        }
        
        // Apply severity filter
        if (!string.IsNullOrEmpty(_selectedSeverity))
        {
            allLogs = allLogs.Where(l => 
                string.Equals(l.SeverityText, _selectedSeverity, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }
        
        // Apply text search filter (body + attributes)
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            allLogs = ApplyTextSearch(allLogs, _searchText);
        }
        
        _logs = allLogs;
        UpdateChartData(start, end);
        _dataLoading = false;
    }

    private List<LogRecordModel> ApplyTextSearch(List<LogRecordModel> logs, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return logs;

        var terms = searchText.Split(new[] { " AND " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        
        foreach (var term in terms)
        {
            var cleanTerm = term.Trim();
            
            // Attribute search: key=value or key:value
            if (cleanTerm.Contains('=') || cleanTerm.Contains(':'))
            {
                logs = ApplyAttributeFilter(logs, cleanTerm);
            }
            // Free text search in body
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
        var value = parts[1].Trim().Trim('"', '\''); // Remove quotes if present
        var isExactMatch = separator == '=';
        
        return logs.Where(l =>
        {
            // Search in log attributes
            if (l.Attributes?.ContainsKey(key) == true)
            {
                var attrValue = l.Attributes[key]?.ToString() ?? "";
                return isExactMatch 
                    ? attrValue.Equals(value, StringComparison.OrdinalIgnoreCase)
                    : attrValue.Contains(value, StringComparison.OrdinalIgnoreCase);
            }
            
            // Search in resource attributes
            if (l.Resource?.Attributes?.ContainsKey(key) == true)
            {
                var attrValue = l.Resource.Attributes[key]?.ToString() ?? "";
                return isExactMatch 
                    ? attrValue.Equals(value, StringComparison.OrdinalIgnoreCase)
                    : attrValue.Contains(value, StringComparison.OrdinalIgnoreCase);
            }
            
            // Search in scope attributes
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
    
    // Configuration class for bucket and label settings
    private class ChartBucketConfig
    {
        public TimeSpan BucketSize { get; set; }
        public string BucketLabel { get; set; }
        public Func<DateTime, bool> LabelBoundaryCondition { get; set; }
        public string LabelFormat { get; set; }
    }
    
    private void UpdateChartData(DateTime start, DateTime end)
    {
        if (_logs.Count == 0)
        {
            _chartSeries = new List<ChartSeries>();
            _chartXAxisLabels = Array.Empty<string>();
            _chartColors = Array.Empty<string>();
            _chartOptions = new ChartOptions { YAxisTicks = 10 };
            return;
        }

        var duration = end - start;
        var totalHours = duration.TotalHours;

        // Determine bucket configuration based on time range
        ChartBucketConfig config;
        
        if (totalHours <= 24)
        {
            // ≤ 24 hours: hourly buckets, label every day boundary (midnight)
            config = new ChartBucketConfig
            {
                BucketSize = TimeSpan.FromMinutes(10),
                BucketLabel = "Hour",
                LabelBoundaryCondition = dt => dt.Minute == 0, // Every hour
                LabelFormat = "HH:mm"
            };
        }
        else if (totalHours <= 24 * 7)
        {
            // ≤ 7 days: 6-hour buckets, label at midnight of each day
            config = new ChartBucketConfig
            {
                BucketSize = TimeSpan.FromHours(1),
                BucketLabel = "6 Hours",
                LabelBoundaryCondition = dt => dt.Hour == 0, // Midnight
                LabelFormat = "MM/dd"
            };
        }
        else if (totalHours <= 24 * 30)
        {
            // ≤ 30 days: daily buckets, label at start of each week (Monday)
            config = new ChartBucketConfig
            {
                BucketSize = TimeSpan.FromHours(6),
                BucketLabel = "Day",
                LabelBoundaryCondition = dt => dt.Hour == 0 && dt.Day % 2 == 0,
                LabelFormat = "MM/dd"
            };
        }
        else if (totalHours <= 24 * 90)
        {
            // ≤ 90 days: weekly buckets, label at start of each month
            config = new ChartBucketConfig
            {
                BucketSize = TimeSpan.FromHours(12),
                BucketLabel = "Week",
                LabelBoundaryCondition = dt => dt.Hour == 0 && dt.DayOfWeek == DayOfWeek.Monday, // Every Week
                LabelFormat = "MM/dd"
            };
        }
        else if (totalHours <= 24 * 180)
        {
            // ≤ 90 days: weekly buckets, label at start of each month
            config = new ChartBucketConfig
            {
                BucketSize = TimeSpan.FromDays(1),
                BucketLabel = "Week",
                LabelBoundaryCondition = dt => dt.Hour == 0 && (dt.Day == 1 || dt.Day == 15), 
                LabelFormat = "MM/dd"
            };
        }
        else if (totalHours <= 24 * 270)
        {
            // ≤ 90 days: weekly buckets, label at start of each month
            config = new ChartBucketConfig
            {
                BucketSize = TimeSpan.FromDays(2),
                BucketLabel = "Week",
                LabelBoundaryCondition = dt => dt.Hour == 0 && dt.Day == 1 || dt.Day == 2, 
                LabelFormat = "MM/dd"
            };
        }
        else if (totalHours <= 24 * 365)
        {
            // ≤ 1 year: weekly buckets, label at start of each month
            config = new ChartBucketConfig
            {
                BucketSize = TimeSpan.FromDays(2),
                BucketLabel = "Week",
                LabelBoundaryCondition = dt => dt.Hour <= 0 && (dt.Day == 1 || dt.Day == 2), // First of month
                LabelFormat = "MM/dd"
            };
        }
        else
        {
            // > 1 year: monthly buckets, label every month
            config = new ChartBucketConfig
            {
                BucketSize = TimeSpan.FromDays(30),
                BucketLabel = "Month",
                LabelBoundaryCondition = dt => true, // Label all months
                LabelFormat = "MM/yyyy"
            };
        }

        // Create time buckets with severity counts
        var buckets = new Dictionary<DateTime, Dictionary<string, int>>();
        var currentBucket = start;
        
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

        // Count logs in each bucket by severity
        foreach (var log in _logs.Where(l => l.TimeUnixNano.HasValue))
        {
            var logTime = UnixNanoToDateTime(log.TimeUnixNano.Value);
            var severity = log.SeverityText ?? "Debug";
            
            // Find the appropriate bucket
            var bucketKey = buckets.Keys
                .Where(b => logTime >= b && logTime < b.Add(config.BucketSize))
                .FirstOrDefault();

            if (bucketKey != default && buckets.ContainsKey(bucketKey))
            {
                if (buckets[bucketKey].ContainsKey(severity))
                {
                    buckets[bucketKey][severity]++;
                }
                else
                {
                    // If severity not in our predefined list, add to Debug
                    buckets[bucketKey]["Debug"]++;
                }
            }
        }

        // Prepare chart data
        var sortedBuckets = buckets.OrderBy(b => b.Key).ToList();
        
        // Create labels using boundary condition
        _chartXAxisLabels = sortedBuckets.Select(b =>
        {
            if (config.LabelBoundaryCondition(b.Key))
            {
                return b.Key.ToString(config.LabelFormat);
            }
            return ""; // Empty string for non-boundary labels
        }).ToArray();
        
        // Create a series for each severity level
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
        
        // Calculate max value across all buckets for Y-axis ticks
        var maxValue = sortedBuckets.Max(b => b.Value.Values.Sum());
        int yAxisTicks;

        if (maxValue < 10)
        {
            yAxisTicks = 1;
        }
        else if (maxValue < 50)
        {
            yAxisTicks = 5;
        }
        else if (maxValue < 100)
        {
            yAxisTicks = 10;
        }
        else if (maxValue < 500)
        {
            yAxisTicks = 50;
        }
        else if (maxValue < 1000)
        {
            yAxisTicks = 100;
        }
        else if (maxValue < 5000)
        {
            yAxisTicks = 500;
        }
        else if (maxValue < 10000)
        {
            yAxisTicks = 1000;
        }
        else if (maxValue < 50000)
        {
            yAxisTicks = 5000;
        }
        else if (maxValue < 100000)
        {
            yAxisTicks = 10000;
        }
        else
        {
            yAxisTicks = 50000;
        }
        
        _chartOptions = new ChartOptions 
        { 
            YAxisTicks = yAxisTicks,
            ChartPalette = _chartColors 
        };
    }
    
    private DateTime UnixNanoToDateTime(long unixNano)
    {
        // Convert nanoseconds to ticks (1 tick = 100 nanoseconds)
        long ticks = unixNano / 100;
        
        // Unix epoch start: January 1, 1970
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return epoch.AddTicks(ticks).ToLocalTime();
    }
}