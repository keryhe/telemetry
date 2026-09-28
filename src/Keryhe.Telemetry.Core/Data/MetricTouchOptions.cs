namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Options for <see cref="MetricTouchWorker"/>. Bound from the <c>Telemetry:MetricTouch</c>
/// configuration section (list-pages-server-side plan, Phase 5, decision 27). Kept separate from
/// <see cref="TenantResolutionOptions"/> even though both are periodic best-effort flush workers:
/// api-key touches are driven by tenant resolution traffic, metric touches by ingestion flushes,
/// and the two have no reason to share one flush cadence.
/// </summary>
public sealed class MetricTouchOptions
{
    /// <summary>Configuration section name these options bind from.</summary>
    public const string SectionName = "Telemetry:MetricTouch";

    /// <summary>Seconds between <c>metric_last_seen</c> flush cycles. Defaults to 60.</summary>
    public int FlushIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum rows per <see cref="IMetricTouchStore.TouchAsync"/> call. Capped at 4,000 by
    /// default — the same threshold the retention sweeps use, below SQL Server's lock-escalation
    /// point (decision 27). A drain larger than this is split into consecutive batches, each
    /// sorted by metric id, so concurrent collector instances always lock in the same order.
    /// </summary>
    public int MaxBatchSize { get; set; } = 4000;
}
