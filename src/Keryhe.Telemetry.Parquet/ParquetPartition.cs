namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// One tenant-day-hour folder of one table (README "Layout"). Hours and days are UTC. An <see cref="Hour"/> of
/// <see cref="DayHour"/> names the day's <c>_day</c> folder, where major compaction merges a closed day (phase 4).
/// </summary>
public readonly record struct ParquetPartition(string Table, long TenantId, DateOnly Date, int Hour)
{
    /// <summary>The partition a row with this partition time (Unix nanoseconds, clamped to the epoch) belongs to.</summary>
    public static ParquetPartition For(string table, long tenantId, long unixNano)
    {
        var time = DateTime.UnixEpoch.AddTicks(Math.Max(0L, unixNano) / 100);
        return new ParquetPartition(table, tenantId, DateOnly.FromDateTime(time), time.Hour);
    }

    /// <summary>The <see cref="Hour"/> of a day partition.</summary>
    public const int DayHour = -1;

    public bool IsDay => Hour == DayHour;

    /// <summary>The <c>_day</c> folder of this partition's day.</summary>
    public ParquetPartition DayPartition => this with { Hour = DayHour };

    /// <summary>The first nanosecond of an hour partition (UTC, Unix nanoseconds); of a day partition, its day's.</summary>
    public long StartNano => (Date.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber) * 24L * NanosPerHour + Math.Max(0, Hour) * NanosPerHour;

    /// <summary>One past the last nanosecond of the hour (or the day).</summary>
    public long EndNano => StartNano + (IsDay ? 24 : 1) * NanosPerHour;

    internal const long NanosPerHour = 3_600_000_000_000L;

    /// <summary>The partition's folder relative to the root, with forward slashes.</summary>
    public string RelativePath
        => IsDay ? $"{Table}/tenant_id={TenantId}/date={Date:yyyy-MM-dd}/_day"
                 : $"{Table}/tenant_id={TenantId}/date={Date:yyyy-MM-dd}/hour={Hour:D2}";

    public override string ToString() => RelativePath;
}
