namespace Keryhe.Telemetry.Core;

/// <summary>
/// Folds the minute rollup into the hour tier (plans/summary-rollups.md, Phase 4). Registered only by a provider
/// that has an hour tier (MySQL, the one the measurement gate named); driven by the API host's
/// <c>RollupCompactionWorker</c>, which idles when nothing is registered.
/// </summary>
public interface IRollupCompactor
{
    /// <summary>
    /// One compaction run as of <paramref name="nowUtc"/>: under a database lock so two API instances never compact at once,
    /// for each signal re-folds every closed hour from <c>min(compacted_through, now - RecompactHours)</c> to the last closed
    /// hour and advances <c>compacted_through</c>. Returns the hours folded (0 when another instance held the lock).
    /// </summary>
    Task<int> CompactAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}
