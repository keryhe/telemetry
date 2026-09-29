using System.Diagnostics;

namespace Keryhe.Telemetry.StressTests.Load;

/// <summary>
/// Open-loop scheduler (stress-test plan, Phase 2): fires ticks on a fixed schedule regardless of
/// how long earlier work took, so a slow server shows up as rising latency and backlog rather than
/// being hidden by a lower offered load. The callback runs synchronously on the scheduler thread and
/// must not block; it starts the work and returns.
///
/// The rate can change mid-run (<see cref="Rate"/>): the schedule is re-based at the change so a
/// step in the ramp scenario takes effect immediately, without a burst to "catch up".
/// </summary>
public sealed class RateController
{
    private readonly object _gate = new();
    private double _rate;
    private long _baseTicks;
    private long _baseTimestamp = Stopwatch.GetTimestamp();

    public RateController(double ticksPerSecond) => _rate = ticksPerSecond;

    /// <summary>Ticks per second. Setting it re-bases the schedule.</summary>
    public double Rate
    {
        get { lock (_gate) return _rate; }
        set
        {
            lock (_gate)
            {
                _baseTicks += (long)Math.Floor(_rate * Stopwatch.GetElapsedTime(_baseTimestamp).TotalSeconds);
                _baseTimestamp = Stopwatch.GetTimestamp();
                _rate = value;
            }
        }
    }

    /// <summary>Ticks skipped because the scheduler itself fell too far behind to fire them (load-tool saturation).</summary>
    public long SkippedTicks { get; private set; }

    /// <param name="onTick">Called with the tick's sequence number and how late it fired against its scheduled time.</param>
    public async Task RunAsync(Action<long, TimeSpan> onTick, CancellationToken cancellationToken)
    {
        long issued = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            double rate;
            long due;
            double elapsed;
            lock (_gate)
            {
                rate = _rate;
                elapsed = Stopwatch.GetElapsedTime(_baseTimestamp).TotalSeconds;
                due = _baseTicks + (long)Math.Floor(rate * elapsed);
            }

            if (rate > 0 && due > issued)
            {
                // More than a second's worth overdue: the scheduler is saturated. Fire what fits in
                // the last second and record the rest as skipped instead of bursting without bound.
                var maxBacklog = (long)Math.Ceiling(rate) + 1;
                if (due - issued > maxBacklog)
                {
                    var skip = due - issued - maxBacklog;
                    SkippedTicks += skip;
                    issued += skip;
                }

                for (; issued < due; issued++)
                {
                    var scheduledAt = rate > 0 ? (issued + 1 - _baseTicks) / rate : 0;
                    var lag = TimeSpan.FromSeconds(Math.Max(0, elapsed - scheduledAt));
                    onTick(issued, lag);
                }
            }

            var wait = rate > 0 ? Math.Clamp(1000.0 / rate / 2, 1, 20) : 50;
            try { await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
