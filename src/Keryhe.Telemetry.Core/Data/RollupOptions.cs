namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Options for the summary rollups (plans/summary-rollups.md). Bound from <c>Telemetry:Rollup</c>.
/// The collector's <c>RollupWorker</c> reads the write-side values; the API reads
/// <see cref="FlushIntervalSeconds"/>, <see cref="CloseGraceSeconds"/> and
/// <see cref="ArrivalMarginSeconds"/> to compute <c>writtenThrough</c>, so a host that changes the
/// first two on the collector must change them on the API too.
/// </summary>
public sealed class RollupOptions
{
    public const string SectionName = "Telemetry:Rollup";

    /// <summary>Seconds between rollup appends. Defaults to 15.</summary>
    public int FlushIntervalSeconds { get; set; } = 15;

    /// <summary>A minute is written once its end is at least this many seconds in the past. Defaults to 30.</summary>
    public int CloseGraceSeconds { get; set; } = 30;

    /// <summary>Rows kept in memory while appends fail; beyond this the oldest are dropped and counted. Defaults to 200,000.</summary>
    public int MaxBufferedRows { get; set; } = 200_000;

    /// <summary>Rows per append call, kept under SQL Server's lock-escalation threshold. Defaults to 4,000.</summary>
    public int MaxBatchSize { get; set; } = 4000;

    /// <summary>API side: extra margin for request duration, export delay and queue lag before a minute counts as written. Defaults to 60.</summary>
    public int ArrivalMarginSeconds { get; set; } = 60;

    /// <summary>API side, hour tier (MySQL): seconds between compaction runs. Defaults to 300.</summary>
    public int CompactionIntervalSeconds { get; set; } = 300;

    /// <summary>API side, hour tier (MySQL): how many hours back each compaction run re-folds, so late minute rows reach the hour tier. Defaults to 6.</summary>
    public int RecompactHours { get; set; } = 6;
}
