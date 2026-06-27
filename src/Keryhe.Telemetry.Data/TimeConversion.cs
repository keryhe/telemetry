using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

/// <summary>
/// Conversions between Unix nanosecond timestamps (how OTLP time is stored) and
/// <see cref="DateTime"/>. Plain, provider-agnostic, and EF-free — used by the write
/// adapters' delete-by-time-range paths and by the read repositories.
/// </summary>
public static class TimeConversion
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
