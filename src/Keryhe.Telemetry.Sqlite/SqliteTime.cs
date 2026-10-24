using System.Globalization;

namespace Keryhe.Telemetry.Sqlite;

/// <summary>
/// The one place that knows how the SQLite control plane stores time. Every timestamp column is <c>TEXT</c> in UTC in exactly
/// one layout, <c>yyyy-MM-dd HH:mm:ss.fffffff</c> (seven fractional digits, always), so a comparison in SQL (the alert cooldown
/// claim) is a plain text comparison and the sqlite3 CLI shows readable values. Microsoft.Data.Sqlite's own DateTime
/// binding trims trailing zeros (<c>.FFFFFFF</c>), which would break that, so repositories bind <see cref="ToColumn(DateTime)"/>'s
/// string instead. The column defaults (<c>strftime('%Y-%m-%d %H:%M:%f0000', 'now')</c>) write the same layout, with
/// millisecond precision.
/// </summary>
public static class SqliteTime
{
    /// <summary>The .NET format of a stored timestamp.</summary>
    public const string Format = "yyyy-MM-dd HH:mm:ss.fffffff";

    /// <summary>SQL expression for the current UTC time in the stored layout.</summary>
    public const string NowSql = "strftime('%Y-%m-%d %H:%M:%f0000', 'now')";

    /// <summary>
    /// SQL expression for "now minus <paramref name="minutesParameter"/> minutes" in the stored layout.
    /// </summary>
    public static string NowMinusMinutesSql(string minutesParameter) =>
        $"strftime('%Y-%m-%d %H:%M:%f0000', 'now', printf('-%d minutes', {minutesParameter}))";

    /// <summary>A UTC <see cref="DateTime"/> as the stored text. An Unspecified kind is taken to be UTC, a Local one converted.</summary>
    public static string ToColumn(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return utc.ToString(Format, CultureInfo.InvariantCulture);
    }

    /// <summary>A <see cref="DateTimeOffset"/> as the stored text, converted to UTC first so no offset suffix is ever written.</summary>
    public static string ToColumn(DateTimeOffset value) => ToColumn(value.UtcDateTime);

    /// <summary>Parses a stored timestamp (any layout SQLite's own functions or the column defaults produce) as a UTC <see cref="DateTime"/>.</summary>
    public static DateTime FromColumn(string value) =>
        DateTime.SpecifyKind(
            DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            DateTimeKind.Utc);

    /// <summary>A <see cref="DateTime"/> the driver handed back (Kind Unspecified) is UTC by convention; stamp it so.</summary>
    public static DateTime FromColumn(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
