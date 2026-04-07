namespace Keryhe.Telemetry.Data.Access;

/// <summary>
/// Utility helpers for converting between Unix nanoseconds and DateTime.
/// </summary>
public static class OpenTelemetryDbContextExtensions
{
    /// <summary>
    /// Converts Unix nanoseconds to DateTime (UTC).
    /// </summary>
    public static DateTime UnixNanoToDateTime(long unixNano)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(unixNano / 1_000_000).UtcDateTime;
    }

    /// <summary>
    /// Converts DateTime to Unix nanoseconds.
    /// </summary>
    public static long DateTimeToUnixNano(DateTime dateTime)
    {
        return new DateTimeOffset(dateTime).ToUnixTimeMilliseconds() * 1_000_000;
    }
}
