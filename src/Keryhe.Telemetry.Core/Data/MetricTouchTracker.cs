using System.Collections.Concurrent;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Process-lifetime map of <c>metric_id → newest observed time_unix_nano</c> since the last
/// <see cref="Drain"/> (list-pages-server-side plan, Phase 5, decision 27). Each relational
/// provider's bulk writer calls <see cref="MarkTouched"/> once per metric in a flushed batch,
/// after its data-point insert has committed; <see cref="MetricTouchWorker"/> drains and
/// bulk-writes <c>metric_last_seen.last_seen_unix_nano</c> via <see cref="IMetricTouchStore"/> on
/// a timer. Not registered/used on ClickHouse — its <c>metric_last_seen</c> table is fed by
/// materialized views on the data-point tables themselves (see <see cref="IMetricTouchStore"/>'s
/// doc comment).
///
/// Mirrors <see cref="ApiKeyTouchTracker"/>'s shape, with one difference: a key here carries a
/// VALUE (the newest timestamp seen for that metric), not just presence, so a metric touched
/// several times between drains must keep the maximum, not just any one of them.
/// <see cref="Drain"/> swaps the backing dictionary atomically via
/// <see cref="Interlocked.Exchange"/>, so a <see cref="MarkTouched"/> call that read the OLD
/// dictionary reference immediately before a concurrent drain can still write into it after the
/// swap, losing that one touch for this interval — acceptable for the same reason
/// <see cref="ApiKeyTouchTracker"/> accepts it: the field this drives is already an approximation
/// (decision 27's "seen in range"), and a metric still being written gets touched again on its
/// next flush.
/// </summary>
public sealed class MetricTouchTracker
{
    private ConcurrentDictionary<long, long> _touched = new();

    /// <summary>Records that <paramref name="metricId"/> was seen with <paramref name="timeUnixNano"/>, keeping the max if already recorded this interval.</summary>
    public void MarkTouched(long metricId, long timeUnixNano)
    {
        _touched.AddOrUpdate(metricId, timeUnixNano, (_, existing) => Math.Max(existing, timeUnixNano));
    }

    /// <summary>Atomically swaps out the current map and returns what had accumulated since the last drain.</summary>
    public IReadOnlyCollection<KeyValuePair<long, long>> Drain()
        => Interlocked.Exchange(ref _touched, new ConcurrentDictionary<long, long>()).ToList();
}
