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
    // Chart data
    private List<ChartSeries> _chartSeries = new();
    private string[] _chartXAxisLabels = Array.Empty<string>();
    private string[] _chartColors = Array.Empty<string>();
    private ChartOptions _chartOptions = new() ;
    // Severity levels in consistent order
    private readonly string[] _severityLevels = new[] { "Trace", "Debug", "Information", "Warning", "Error", "Fatal" };
    
    [Inject]
    private ILogService LogService { get; set; }

    protected override async Task OnInitializedAsync()
    {
        _dataLoading = true;
        
        if (_dateRange?.Start == null || _dateRange?.End == null)
        {
            return;
        }
        var start = _dateRange.Start.Value;
        var end =_dateRange.End.Value.AddDays(1);
        _logs = GetMockLogRecords(start, end); //await LogService.GetLogRecordsByTimeRangeAsync(start, end);
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
            "INFORMATION" => Colors.BlueGray.Lighten2,
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

        if (_dateRange?.Start == null || _dateRange?.End == null)
        {
            return;
        }
        var start = _dateRange.Start.Value;
        var end =_dateRange.End.Value.AddDays(1);
        _logs = GetMockLogRecords(start, end); //await LogService.GetLogRecordsByTimeRangeAsync(start, end);
        UpdateChartData(start, end);
        _dataLoading = false;
    }

    private Dictionary<string, object> GetDetails(LogRecordModel? expandedItem)
    {
        Dictionary<string, object> details = new();

        if (expandedItem == null)
        {
            return details;
        }

        if (expandedItem.Resource != null )
        {
            foreach (var item in expandedItem.Resource.Attributes)
            {
                details.Add(item.Key, item.Value);
            }
        }

        if (expandedItem.InstrumentationScope != null)
        {
            foreach (var item in expandedItem.InstrumentationScope.Attributes)
            {
                details.Add(item.Key, item.Value);
            }
        }

        foreach (var item in expandedItem.Attributes)
        {
            details.Add(item.Key, item.Value);
        }

        return details;
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
    
    private List<LogRecordModel> GetMockLogRecords(DateTime? start, DateTime? end)
{
    var logs = new List<LogRecordModel>();
    var random = new Random(42); // Fixed seed for consistent results
    
    if (start == null || end == null)
    {
        start = DateTime.Today.AddDays(-7);
        end = DateTime.Today;
    }
    
    var severities = new[] { "Debug", "Information", "Warning", "Error", "FATAL" };
    var severityWeights = new[] { 30, 50, 15, 4, 1 }; // Percentage distribution
    
    var messages = new Dictionary<string, string[]>
    {
        ["Debug"] = new[] 
        { 
            "Processing request started",
            "Cache lookup performed",
            "Query execution began",
            "Validation step completed"
        },
        ["Information"] = new[] 
        { 
            "User logged in successfully",
            "Data saved to database",
            "Request completed successfully",
            "Configuration loaded",
            "Service started"
        },
        ["Warning"] = new[] 
        { 
            "Response time exceeded threshold",
            "Cache miss occurred",
            "Retry attempt initiated",
            "Deprecated API used"
        },
        ["Error"] = new[] 
        { 
            "Database connection failed",
            "Null reference exception caught",
            "API call timeout",
            "Authentication failed"
        },
        ["FATAL"] = new[] 
        { 
            "Critical system failure",
            "Unable to connect to required service",
            "Data corruption detected"
        }
    };
    
    // Generate logs for each day in the range
    var currentDay = start.Value.Date;
    
    while (currentDay <= end.Value.Date)
    {
        // Random number of logs for this day (between 50-200 per day)
        var logsForDay = random.Next(50, 200);
        
        for (int i = 0; i < logsForDay; i++)
        {
            // Random time within this specific day
            var randomHour = random.Next(0, 24);
            var randomMinute = random.Next(0, 60);
            var randomSecond = random.Next(0, 60);
            var randomMillisecond = random.Next(0, 1000);
            
            var logTime = currentDay
                .AddHours(randomHour)
                .AddMinutes(randomMinute)
                .AddSeconds(randomSecond)
                .AddMilliseconds(randomMillisecond);
            
            // Don't create logs beyond the end date/time
            if (logTime > end.Value)
                continue;
            
            // Select severity based on weighted distribution
            var severityRoll = random.Next(0, 100);
            var cumulativeWeight = 0;
            var selectedSeverity = "Information";
            
            for (int j = 0; j < severities.Length; j++)
            {
                cumulativeWeight += severityWeights[j];
                if (severityRoll < cumulativeWeight)
                {
                    selectedSeverity = severities[j];
                    break;
                }
            }
            
            // Select random message for this severity
            var messageArray = messages[selectedSeverity];
            var message = messageArray[random.Next(messageArray.Length)];
            
            // Convert DateTime to Unix nanoseconds
            var unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var timeSpan = logTime.ToUniversalTime() - unixEpoch;
            var unixNano = (long)(timeSpan.TotalSeconds * 1_000_000_000);
            
            var log = new LogRecordModel
            {
                TimeUnixNano = unixNano,
                SeverityText = selectedSeverity,
                BodyValue = message,
                TraceIdHex = Guid.NewGuid().ToString("N").Substring(0, 32),
                SpanIdHex = Guid.NewGuid().ToString("N").Substring(0, 16),
                Attributes = new Dictionary<string, object>
                {
                    ["thread.id"] = random.Next(1, 20),
                    ["http.status_code"] = random.Next(200, 505),
                    ["user.id"] = $"user_{random.Next(1, 100)}"
                },
                InstrumentationScope = new InstrumentationScopeModel
                {
                    Name = "Keryhe.Telemetry.MockService",
                    Attributes = new Dictionary<string, object>
                    {
                        ["version"] = "1.0.0"
                    }
                },
                Resource = new ResourceModel
                {
                    Attributes = new Dictionary<string, object>
                    {
                        ["service.name"] = "telemetry-service",
                        ["host.name"] = $"server-{random.Next(1, 5)}"
                    }
                }
            };
            
            logs.Add(log);
        }
        
        // Move to next day
        currentDay = currentDay.AddDays(1);
    }
    
    // Sort by time
    return logs.OrderBy(l => l.TimeUnixNano).ToList();
}
}