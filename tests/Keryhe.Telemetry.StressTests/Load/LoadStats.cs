using System.Diagnostics;

namespace Keryhe.Telemetry.StressTests.Load;

/// <summary>One second of client-side activity for one signal, for the report's aligned timelines.</summary>
public sealed record SecondSample(
    int Second, long OfferedRecords, long AckedRecords, long RejectedRecords, long FailedRecords,
    long NotSentRecords, int Exports, double LatencyMeanMs, double LatencyMaxMs);

public sealed record SignalSummary(
    string Signal,
    long OfferedRecords, long AckedRecords, long RejectedRecords, long FailedRecords, long NotSentRecords,
    long ExportsOk, long ExportsRejected, long ExportsFailed,
    IReadOnlyDictionary<string, long> GrpcStatusCounts,
    LatencySummary Latency,
    double DispatchLagMeanMs, double DispatchLagMaxMs,
    long SchedulerSkippedExports,
    double OfferedPerSecond, double AckedPerSecond,
    IReadOnlyList<SecondSample> Timeline);

/// <summary>What one signal did during a window (the measured window, or one ramp step): records by outcome, export outcomes, latency.</summary>
public sealed record WindowSummary(
    string Signal, double Seconds,
    long OfferedRecords, long AckedRecords, long RejectedRecords, long FailedRecords, long NotSentRecords,
    long ExportsOk, long ExportsRejected, long ExportsFailed, LatencySummary Latency)
{
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

    private readonly object _gate = new();
    private readonly List<Second> _seconds = [];
    private readonly Dictionary<string, long> _status = [];
    private readonly LatencyHistogram _latency = new();
    private long _offered, _acked, _rejected, _failed, _notSent, _ok, _partial, _errored;
    private double _lagSum, _lagMax;
    private long _lagCount;
    private long _startTimestamp;

    // The active window, if any. Counted alongside the whole-run totals so the headline numbers can exclude warm-up.
    private LatencyHistogram? _windowLatency;
    private long _windowStart;
    private long _wOffered, _wAcked, _wRejected, _wFailed, _wNotSent, _wOk, _wPartial, _wErrored;

    /// <summary>Starts a fresh window (ending any previous one without a summary).</summary>
    public void BeginWindow()
    {
        lock (_gate)
        {
            _windowLatency = new LatencyHistogram();
            _windowStart = Stopwatch.GetTimestamp();
            _wOffered = _wAcked = _wRejected = _wFailed = _wNotSent = _wOk = _wPartial = _wErrored = 0;
        }
    }

    public WindowSummary EndWindow()
    {
        lock (_gate)
        {
            var summary = new WindowSummary(signal, Stopwatch.GetElapsedTime(_windowStart).TotalSeconds,
                _wOffered + _wNotSent, _wAcked, _wRejected, _wFailed, _wNotSent, _wOk, _wPartial, _wErrored,
                _windowLatency?.Summarize() ?? new LatencySummary(0, 0, 0, 0, 0, 0));
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

    public void Completed(long records, double latencyMs, ExportOutcome outcome, string statusName)
    {
        _latency.Record(latencyMs);
        lock (_gate)
        {
            var b = Bucket();
            if (_windowLatency is not null)
            {
                _windowLatency.Record(latencyMs);
                switch (outcome)
                {
                    case ExportOutcome.Ok: _wOk++; _wAcked += records; break;
                    case ExportOutcome.Rejected: _wPartial++; _wRejected += records; break;
                    default: _wErrored++; _wFailed += records; break;
                }
            }
            b.Exports++; b.LatencySum += latencyMs; b.LatencyMax = Math.Max(b.LatencyMax, latencyMs);
            _status[statusName] = _status.GetValueOrDefault(statusName) + 1;
            switch (outcome)
            {
                case ExportOutcome.Ok: _ok++; _acked += records; b.Acked += records; break;
                case ExportOutcome.Rejected: _partial++; _rejected += records; b.Rejected += records; break;
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
                    s.Exports, s.Exports == 0 ? 0 : s.LatencySum / s.Exports, s.LatencyMax)).ToList());
        }
    }
}

public enum ExportOutcome { Ok, Rejected, Failed }
