namespace Keryhe.Telemetry.Core;

/// <summary>
/// Shared timestamp conversion utilities for OpenTelemetry nanosecond timestamps.
/// </summary>
public static class TimestampConverter
{
    /// <summary>
    /// Converts a Unix nanosecond timestamp to a UTC DateTime.
    /// </summary>
    public static DateTime UnixNanoToUtcDateTime(long unixNano)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixNano / 1_000_000).UtcDateTime;
}
