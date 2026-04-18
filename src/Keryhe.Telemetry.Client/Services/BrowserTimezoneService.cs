namespace Keryhe.Telemetry.Client.Services;

/// <summary>
/// Scoped service that holds the browser's local timezone, captured once via JS interop
/// after the first render. Used to convert UTC DateTimes to local time for display.
/// </summary>
public class BrowserTimezoneService
{
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;

    public void SetTimezone(string ianaName)
    {
        if (string.IsNullOrWhiteSpace(ianaName)) return;
        try
        {
            _timeZone = TimeZoneInfo.FindSystemTimeZoneById(ianaName);
        }
        catch
        {
            _timeZone = TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Converts a UTC DateTime to local browser time. Falls back to UTC if the
    /// browser timezone has not yet been captured.
    /// </summary>
    public DateTime ToLocalTime(DateTime utcDateTime)
    {
        if (utcDateTime.Kind == DateTimeKind.Unspecified)
            utcDateTime = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, _timeZone);
    }

    /// <summary>
    /// Converts a local browser DateTime (Kind=Unspecified from pickers) to UTC.
    /// </summary>
    public DateTime ToUtc(DateTime localDateTime)
    {
        var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, _timeZone);
    }
}
