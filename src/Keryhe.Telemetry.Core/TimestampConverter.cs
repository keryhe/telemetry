namespace Keryhe.Telemetry.Core;

/// <summary>
/// Shared timestamp conversion utilities for OpenTelemetry nanosecond timestamps.
/// </summary>
public static class TimestampConverter
{
    /// <summary>
    /// Converts a Unix nanosecond timestamp to a UTC DateTime, preserving precision to the nearest
    /// 100ns tick (the finest <see cref="DateTime"/> can represent). Previously floored to whole
    /// milliseconds via <c>FromUnixTimeMilliseconds(unixNano / 1_000_000)</c>, which made any
    /// sub-millisecond trace duration read as exactly zero (trace-latency-p50 plan, Cause B).
    /// </summary>
    public static DateTime UnixNanoToUtcDateTime(long unixNano)
        => new DateTime(DateTime.UnixEpoch.Ticks + unixNano / 100, DateTimeKind.Utc);
}
