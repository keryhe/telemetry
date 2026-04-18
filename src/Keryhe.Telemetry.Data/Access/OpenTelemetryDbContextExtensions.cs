using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data.Access;

/// <summary>
/// Utility helpers for converting between Unix nanoseconds and DateTime.
/// </summary>
public static class OpenTelemetryDbContextExtensions
{
    /// <summary>
    /// Converts Unix nanoseconds to UTC DateTime.
    /// </summary>
    public static DateTime UnixNanoToDateTime(long unixNano)
        => TimestampConverter.UnixNanoToUtcDateTime(unixNano);

    /// <summary>
    /// Converts DateTime to Unix nanoseconds. Treats Unspecified Kind as UTC.
    /// </summary>
    public static long DateTimeToUnixNano(DateTime dateTime)
    {
        if (dateTime.Kind == DateTimeKind.Unspecified)
            dateTime = DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
        return new DateTimeOffset(dateTime).ToUnixTimeMilliseconds() * 1_000_000;
    }
}
