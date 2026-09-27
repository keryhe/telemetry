namespace Keryhe.Telemetry.Core;

/// <summary>
/// <c>rollup_state</c>'s current row for one signal + granularity (list-pages-server-side plan,
/// Phase 2, decisions 37-38).
/// </summary>
public sealed class RollupStateInfo
{
    public long? CoverageStartUnixNano { get; set; }
    public long RolledUntilUnixNano { get; set; }
    public long RepassedUntilUnixNano { get; set; }
}

/// <summary>
/// The mechanical operations <c>RollupWorker</c> (<c>Keryhe.Telemetry.Api</c>) needs against
/// <c>rollup_state</c>/<c>log_rollup_minute</c>/<c>log_rollup_hour</c> — the lease claim (mirroring
/// <see cref="IAlertRuleRepository.TryClaimFireAsync"/>'s atomic UPDATE), reading the current
/// coverage/progress, and recomputing a closed time range from raw data. One implementation per
/// provider, registered by <c>Add&lt;Provider&gt;ApiServices</c> like every other read-side
/// repository. Only the "logs" signal is used in this phase; the "granularity" parameter is either
/// <c>"minute"</c> or <c>"hour"</c>.
/// </summary>
public interface IRollupRepository
{
    /// <summary>Atomic claim: succeeds only when no other instance's lease is currently valid.</summary>
    Task<bool> TryClaimLeaseAsync(string signal, string granularity, string owner, TimeSpan leaseDuration, CancellationToken ct = default);

    Task<RollupStateInfo?> GetStateAsync(string signal, string granularity, CancellationToken ct = default);

    /// <summary>Sets <c>coverage_start_unix_nano</c> once, on first start — a no-op once it is already set.</summary>
    Task SetCoverageStartAsync(string signal, string granularity, long coverageStartUnixNano, CancellationToken ct = default);

    Task AdvanceRolledUntilAsync(string signal, string granularity, long rolledUntilUnixNano, CancellationToken ct = default);
    Task AdvanceRepassedUntilAsync(string signal, string granularity, long repassedUntilUnixNano, CancellationToken ct = default);

    /// <summary>Deletes then recomputes <c>log_rollup_minute</c> rows for <c>[fromInclusive, toExclusive)</c> from raw <c>log_records</c>.</summary>
    Task RollLogMinutesAsync(long fromInclusive, long toExclusive, CancellationToken ct = default);

    /// <summary>Deletes then recomputes <c>log_rollup_hour</c> rows for <c>[fromInclusive, toExclusive)</c> from <c>log_rollup_minute</c>.</summary>
    Task RollLogHoursAsync(long fromInclusive, long toExclusive, CancellationToken ct = default);

    /// <summary>
    /// Trace half of the rollup worker (list-pages-server-side plan, Phase 3, decision 41). For
    /// every trace with a span starting in <c>[fromInclusive, toExclusive)</c> that has no
    /// null-parent span anywhere in the trace, finds its earliest span and, when that span's own
    /// parent does not exist as any span_id, replaces the minute's <c>orphan_roots</c> rows with
    /// it. Re-running the same range (the 15-minute re-roll) both adds newly-detected orphans and
    /// removes rows whose real root has since arrived.
    /// </summary>
    Task RollOrphanRootsAsync(long fromInclusive, long toExclusive, CancellationToken ct = default);

    /// <summary>
    /// Deletes then recomputes <c>trace_rollup_minute</c> rows for
    /// <c>[fromInclusive, toExclusive)</c>: anchors (null-parent roots plus <c>orphan_roots</c>,
    /// the latter with the late-root <c>NOT EXISTS</c> re-check) starting in the range, aggregated
    /// over each anchor trace's full span set (error flag, whole-trace duration, latency bucket).
    /// Must run after <see cref="RollOrphanRootsAsync"/> for the same range on the same cycle, so
    /// a trace detected as an orphan in this pass is already anchored when this recompute runs.
    /// </summary>
    Task RollTraceMinutesAsync(long fromInclusive, long toExclusive, CancellationToken ct = default);

    /// <summary>Deletes then recomputes <c>trace_rollup_hour</c> rows for <c>[fromInclusive, toExclusive)</c> from <c>trace_rollup_minute</c>.</summary>
    Task RollTraceHoursAsync(long fromInclusive, long toExclusive, CancellationToken ct = default);
}
