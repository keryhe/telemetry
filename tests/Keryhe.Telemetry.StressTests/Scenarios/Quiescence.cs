using Keryhe.Telemetry.StressTests.Observers.Process;
using Keryhe.Telemetry.StressTests.Orchestration;

namespace Keryhe.Telemetry.StressTests.Scenarios;

/// <summary>Decides when ingestion has gone quiet: nothing resident in the channel and nothing flushed for a stable period.</summary>
public sealed class QuiescenceTracker(TimeSpan stableFor)
{
    private double? _lastFlushed;
    private DateTimeOffset? _quietSince;

    /// <param name="resident">Records resident across the ingestion gates, or null when no host reported the gauge yet.</param>
    /// <param name="flushedTotal">Records flushed so far (a counter's running total).</param>
    public bool Update(DateTimeOffset now, double? resident, double flushedTotal)
    {
        var moving = resident is > 0 || (_lastFlushed is { } last && flushedTotal != last);
        _lastFlushed = flushedTotal;
        if (moving) _quietSince = null;
        else _quietSince ??= now;
        return _quietSince is { } since && now - since >= stableFor;
    }
}

public sealed record QuiesceResult(bool Reached, double WaitedSeconds, double TimeoutSeconds, double? ResidentAtEnd);

/// <summary>Reads the ingestion instruments off the hosts' EventPipe stores. The collector (or all-in-one) host carries them; an API-only host has none.</summary>
public static class HostMetricsQuery
{
    private const string Prefix = "keryhe.telemetry.ingestion.";
    private static readonly string[] Signals = ["logs", "traces", "metrics"];

    private static IEnumerable<MetricStore> Stores(HostSet hosts) => hosts.Hosts.Where(h => h.Metrics is not null).Select(h => h.Metrics!.Store);

    /// <summary>Resident records over all signals and hosts (each gauge's latest reading), or null if none has been read.</summary>
    public static double? Resident(HostSet hosts)
    {
        double? total = null;
        foreach (var store in Stores(hosts))
            foreach (var signal in Signals)
                if (store.Latest(Prefix + "resident_records", "signal", signal) is { } s && !double.IsNaN(s.Value))
                    total = (total ?? 0) + s.Value;
        return total;
    }

    public static double Flushed(HostSet hosts) =>
        Stores(hosts).Sum(store => Signals.Sum(signal => store.Total(Prefix + "records_flushed", "signal", signal)));

    public static double Dropped(HostSet hosts, DateTimeOffset from, DateTimeOffset to) =>
        Stores(hosts).Sum(store => Signals.Sum(signal => store.Window(Prefix + "records_dropped", from, to, "signal", signal)
            .Where(s => s.Kind == "counter" && !double.IsNaN(s.Value)).Sum(s => s.Value)));

    /// <summary>The worst signal's gate-wait p95 over the window, averaging each signal's per-interval p95 weighted by how many waits it covered.</summary>
    public static double GateWaitP95Ms(HostSet hosts, DateTimeOffset from, DateTimeOffset to)
    {
        var worst = 0.0;
        foreach (var store in Stores(hosts))
            foreach (var signal in Signals)
            {
                var intervals = store.Window(Prefix + "gate_wait", from, to, "signal", signal).Where(s => s.P95 is not null && s.Count is > 0).ToList();
                var count = intervals.Sum(s => s.Count!.Value);
                if (count > 0) worst = Math.Max(worst, intervals.Sum(s => s.P95!.Value * s.Count!.Value) / count);
            }
        return worst;
    }

    /// <summary>Polls once a second until quiet or <paramref name="timeout"/> passes; a timeout is recorded, not thrown.</summary>
    public static async Task<QuiesceResult> WaitForQuiescenceAsync(HostSet hosts, TimeSpan stableFor, TimeSpan timeout, CancellationToken ct)
    {
        var tracker = new QuiescenceTracker(stableFor);
        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            var resident = Resident(hosts);
            if (tracker.Update(now, resident, Flushed(hosts)))
                return new QuiesceResult(true, (now - started).TotalSeconds, timeout.TotalSeconds, resident);
            if (now - started >= timeout)
                return new QuiesceResult(false, (now - started).TotalSeconds, timeout.TotalSeconds, resident);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }
}
