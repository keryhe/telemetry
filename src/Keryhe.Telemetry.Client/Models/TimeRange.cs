namespace Keryhe.Telemetry.Client.Models;

/// <summary>
/// Standard time range options for metric viewing
/// </summary>
public enum TimeRange
{
    Last1Hour,
    Last6Hours,
    Last24Hours,
    Last7Days,
    Last30Days,
    Custom
}

/// <summary>
/// Extension methods for TimeRange enum
/// </summary>
public static class TimeRangeExtensions
{
    /// <summary>
    /// Converts TimeRange enum to DateTime start and end values
    /// </summary>
    public static (DateTime Start, DateTime End) ToDateTimeRange(this TimeRange timeRange)
    {
        var end = DateTime.UtcNow;
        var start = timeRange switch
        {
            TimeRange.Last1Hour => end.AddHours(-1),
            TimeRange.Last6Hours => end.AddHours(-6),
            TimeRange.Last24Hours => end.AddHours(-24),
            TimeRange.Last7Days => end.AddDays(-7),
            TimeRange.Last30Days => end.AddDays(-30),
            TimeRange.Custom => throw new InvalidOperationException("Custom range requires explicit start/end; use TimeRangeState.GetDateTimeRange() instead of ToDateTimeRange()."),
            _ => end.AddHours(-1)
        };
        
        return (start, end);
    }

    /// <summary>
    /// Gets display name for TimeRange
    /// </summary>
    public static string GetDisplayName(this TimeRange timeRange)
    {
        return timeRange switch
        {
            TimeRange.Last1Hour => "Last 1 Hour",
            TimeRange.Last6Hours => "Last 6 Hours",
            TimeRange.Last24Hours => "Last 24 Hours",
            TimeRange.Last7Days => "Last 7 Days",
            TimeRange.Last30Days => "Last 30 Days",
            TimeRange.Custom => "Custom Range",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Gets recommended refresh interval in seconds for TimeRange
    /// </summary>
    public static int GetRecommendedRefreshInterval(this TimeRange timeRange)
    {
        return timeRange switch
        {
            TimeRange.Last1Hour => 30,      // 30 seconds
            TimeRange.Last6Hours => 60,     // 1 minute
            TimeRange.Last24Hours => 300,   // 5 minutes
            TimeRange.Last7Days => 900,     // 15 minutes
            TimeRange.Last30Days => 1800,   // 30 minutes
            TimeRange.Custom => 60,         // 1 minute
            _ => 60
        };
    }
}
