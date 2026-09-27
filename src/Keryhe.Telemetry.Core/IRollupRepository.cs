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
}
