using System.Diagnostics;

namespace Keryhe.Telemetry.StressTests.Load;

/// <summary>One second of client-side activity for one signal, for the report's aligned timelines.</summary>
public sealed record SecondSample(
    int Second, long OfferedRecords, long AckedRecords, long RejectedRecords, long FailedRecords,
    long NotSentRecords, int Exports, double LatencyMeanMs, double LatencyMaxMs);

/// <summary>What one tenant's exports of one signal came to (the whole run): the noisy-tenant check reads these.</summary>
public sealed record TenantSummary(
    int Tenant, long AckedRecords, long ExportsOk, long ExportsThrottled, long ThrottledAttempts, long ExportsFailed,
    LatencySummary Latency, double AckedPerSecond);

public sealed record SignalSummary(
    string Signal,
    long OfferedRecords, long AckedRecords, long RejectedRecords, long FailedRecords, long NotSentRecords,
    long ExportsOk, long ExportsRejected, long ExportsFailed,
    IReadOnlyDictionary<string, long> GrpcStatusCounts,
    LatencySummary Latency,
    double DispatchLagMeanMs, double DispatchLagMaxMs,
    long SchedulerSkippedExports,
    double OfferedPerSecond, double AckedPerSecond,
    IReadOnlyList<SecondSample> Timeline,
    long ThrottledAttempts = 0, long ExportsThrottled = 0, IReadOnlyList<TenantSummary>? Tenants = null);

/// <summary>What one signal did during a window (the measured window, or one ramp step): records by outcome, export outcomes, latency.</summary>
public sealed record WindowSummary(
    string Signal, double Seconds,
    long OfferedRecords, long AckedRecords, long RejectedRecords, long FailedRecords, long NotSentRecords,
    long ExportsOk, long ExportsRejected, long ExportsFailed, LatencySummary Latency,
    long ThrottledAttempts = 0, long ExportsThrottled = 0, long ThrottledRecords = 0)
{
    /// <summary>
    /// Responses of <c>UNAVAILABLE</c> with a <c>RetryInfo</c> (the collector's queue was full) as a percentage of all export attempts that got an
    /// answer. A throttled attempt is retried, so most end as OK exports; <see cref="ExportsThrottled"/> counts the ones still refused at the deadline.
    /// </summary>
    public double ThrottledRatePercent
    {
        get
        {
            var attempts = ThrottledAttempts + ExportsOk + ExportsRejected + ExportsFailed;
            return attempts == 0 ? 0 : 100.0 * ThrottledAttempts / attempts;
        }
    }

    public double OfferedPerSecond => OfferedRecords / Math.Max(Seconds, 1e-9);
    public double AckedPerSecond => AckedRecords / Math.Max(Seconds, 1e-9);

    /// <summary>Failed exports (a gRPC error, not a partial-success rejection) as a percentage of all completed exports.</summary>
    public double ErrorRatePercent
    {
        get
        {
            var total = ExportsOk + ExportsRejected + ExportsFailed;
            return total == 0 ? 0 : 100.0 * ExportsFailed / total;
        }
    }
}

/// <summary>
/// Client-side counters for one signal (stress-test plan, Phase 2): offered vs achieved records,
/// export outcomes and latency, plus a per-second timeline. "Offered" is what the schedule asked for
/// (including exports the load tool itself could not send), "acked" is what the server accepted.
/// </summary>
public sealed class SignalStats(string signal)
{
    private sealed class Second
    {
        public long Offered, Acked, Rejected, Failed, NotSent;
        public int Exports;
        public double LatencySum, LatencyMax;
    }

    private sealed class TenantAcc
    {
        public long Acked, Ok, Throttled, ThrottledAttempts, Failed;
        public readonly LatencyHistogram Latency = new();
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TenantAcc> _tenants = new();
    private readonly object _gate = new();
    private readonly List<Second> _seconds = [];
    private readonly Dictionary<string, long> _status = [];
    private readonly LatencyHistogram _latency = new();
    private long _offered, _acked, _rejected, _failed, _notSent, _ok, _partial, _errored, _throttledAttempts, _throttledExports;
    private double _lagSum, _lagMax;
    private long _lagCount;
    private long _startTimestamp;

    // The active window, if any. Counted alongside the whole-run totals so the headline numbers can exclude warm-up.
    private LatencyHistogram? _windowLatency;
    private long _windowStart;
    private long _wOffered, _wAcked, _wRejected, _wFailed, _wNotSent, _wOk, _wPartial, _wErrored, _wThrottledAttempts, _wThrottledExports, _wThrottledRecords;

    /// <summary>Starts a fresh window (ending any previous one without a summary).</summary>
    public void BeginWindow()
    {
        lock (_gate)
        {
            _windowLatency = new LatencyHistogram();
            _windowStart = Stopwatch.GetTimestamp();
            _wOffered = _wAcked = _wRejected = _wFailed = _wNotSent = _wOk = _wPartial = _wErrored = _wThrottledAttempts = _wThrottledExports = _wThrottledRecords = 0;
        }
    }

    public WindowSummary EndWindow()
    {
        lock (_gate)
        {
            var summary = new WindowSummary(signal, Stopwatch.GetElapsedTime(_windowStart).TotalSeconds,
                _wOffered + _wNotSent, _wAcked, _wRejected, _wFailed, _wNotSent, _wOk, _wPartial, _wErrored,
                _windowLatency?.Summarize() ?? new LatencySummary(0, 0, 0, 0, 0, 0),
                _wThrottledAttempts, _wThrottledExports, _wThrottledRecords);
            _windowLatency = null;
            return summary;
        }
    }

    public void Start() => _startTimestamp = Stopwatch.GetTimestamp();

    private Second Bucket()
    {
        var index = (int)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        while (_seconds.Count <= index) _seconds.Add(new Second());
        return _seconds[index];
    }

    public void Offered(long records, TimeSpan dispatchLag)
    {
        lock (_gate)
        {
            _offered += records;
            if (_windowLatency is not null) _wOffered += records;
            Bucket().Offered += records;
            var lagMs = dispatchLag.TotalMilliseconds;
            _lagSum += lagMs; _lagCount++; _lagMax = Math.Max(_lagMax, lagMs);
        }
    }

    public void NotSent(long records)
    {
        lock (_gate) { _notSent += records; if (_windowLatency is not null) _wNotSent += records; Bucket().NotSent += records; }
    }

    public void Completed(long records, double latencyMs, ExportOutcome outcome, string statusName, int throttledAttempts = 0, int tenantIndex = -1)
    {
        _latency.Record(latencyMs);
        if (tenantIndex >= 0)
        {
            var t = _tenants.GetOrAdd(tenantIndex, _ => new TenantAcc());
            t.Latency.Record(latencyMs);
            Interlocked.Add(ref t.ThrottledAttempts, throttledAttempts);
            switch (outcome)
            {
                case ExportOutcome.Ok: Interlocked.Increment(ref t.Ok); Interlocked.Add(ref t.Acked, records); break;
                case ExportOutcome.Throttled: Interlocked.Increment(ref t.Throttled); break;
                case ExportOutcome.Failed: Interlocked.Increment(ref t.Failed); break;
            }
        }
        lock (_gate)
        {
            var b = Bucket();
            _throttledAttempts += throttledAttempts;
            if (outcome == ExportOutcome.Throttled) _throttledExports++;
            if (_windowLatency is not null)
            {
                _windowLatency.Record(latencyMs);
                _wThrottledAttempts += throttledAttempts;
                switch (outcome)
                {
                    case ExportOutcome.Ok: _wOk++; _wAcked += records; break;
                    case ExportOutcome.Rejected: _wPartial++; _wRejected += records; break;
                    case ExportOutcome.Throttled: _wThrottledExports++; _wThrottledRecords += records; break;
                    default: _wErrored++; _wFailed += records; break;
                }
            }
            b.Exports++; b.LatencySum += latencyMs; b.LatencyMax = Math.Max(b.LatencyMax, latencyMs);
            _status[statusName] = _status.GetValueOrDefault(statusName) + 1;
            switch (outcome)
            {
                case ExportOutcome.Ok: _ok++; _acked += records; b.Acked += records; break;
                case ExportOutcome.Rejected: _partial++; _rejected += records; b.Rejected += records; break;
                case ExportOutcome.Throttled: b.Failed += records; break;   // refused, never enqueued: not acked, and not a server error
                default: _errored++; _failed += records; b.Failed += records; break;
            }
        }
    }

    public SignalSummary Summarize(long schedulerSkippedExports, int recordsPerExport)
    {
        lock (_gate)
        {
            var seconds = Math.Max(1, _seconds.Count);
            return new SignalSummary(signal,
                _offered + _notSent, _acked, _rejected, _failed, _notSent,
                _ok, _partial, _errored,
                new Dictionary<string, long>(_status),
                _latency.Summarize(),
                _lagCount == 0 ? 0 : _lagSum / _lagCount, _lagMax,
                schedulerSkippedExports,
                (_offered + _notSent + schedulerSkippedExports * recordsPerExport) / (double)seconds, _acked / (double)seconds,
                _seconds.Select((s, i) => new SecondSample(i, s.Offered + s.NotSent, s.Acked, s.Rejected, s.Failed, s.NotSent,
                    s.Exports, s.Exports == 0 ? 0 : s.LatencySum / s.Exports, s.LatencyMax)).ToList(),
                _throttledAttempts, _throttledExports,
                _tenants.OrderBy(kv => kv.Key).Select(kv => new TenantSummary(kv.Key, kv.Value.Acked, kv.Value.Ok, kv.Value.Throttled,
                    kv.Value.ThrottledAttempts, kv.Value.Failed, kv.Value.Latency.Summarize(), kv.Value.Acked / (double)seconds)).ToList());
        }
    }
}

/// <summary>
/// Ok: accepted. Rejected: a partial-success refusal of invalid data. Failed: a gRPC error. Throttled: the collector's queue stayed
/// full until the export's deadline (<c>UNAVAILABLE</c> + <c>RetryInfo</c> on every attempt); nothing was enqueued.
/// </summary>
public enum ExportOutcome { Ok, Rejected, Failed, Throttled }
