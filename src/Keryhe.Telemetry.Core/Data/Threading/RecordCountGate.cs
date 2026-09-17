namespace Keryhe.Telemetry.Core.Data.Threading;

/// <summary>
/// Async counting gate that bounds a resident RECORD count rather than a resident BATCH count.
/// <see cref="TelemetryIngestionChannel"/>'s channels are unbounded on their own; a paired gate is
/// what actually applies backpressure, so ingestion memory stays bounded independently of how many
/// records one caller merges into a single write. A bounded-by-batch-count channel cannot do this:
/// a single write can itself carry an arbitrary number of records, entirely at the client's
/// discretion.
///
/// A call whose own <c>count</c> exceeds the gate's capacity is not rejected outright -- it is
/// admitted once the gate is otherwise empty, so one oversized batch cannot deadlock the writer
/// forever. It still blocks every subsequent <see cref="AcquireAsync"/> until that oversized batch
/// is released.
///
/// Wake-ups are signalled, not counted precisely: <see cref="Release"/> pulses at most one waiter,
/// so under concurrent contention a released waiter can "steal" another's wake-up.
/// <see cref="AcquireAsync"/> re-checks on a short timeout regardless of whether it was signalled,
/// so a stolen pulse costs at most that timeout in extra latency, never a permanent stall.
/// </summary>
public sealed class RecordCountGate
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly int _capacity;
    private readonly SemaphoreSlim _signal = new(0);
    private int _current;

    public RecordCountGate(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>Blocks until <paramref name="count"/> records of headroom are available, then reserves them.</summary>
    public async Task AcquireAsync(int count, CancellationToken cancellationToken)
    {
        if (count <= 0) return;

        while (true)
        {
            var current = Volatile.Read(ref _current);
            // Admit unconditionally when the gate is empty, even if count alone exceeds capacity
            // (the "one oversized batch" allowance documented on the type) -- otherwise a caller
            // requesting more than _capacity could never be admitted and would wait forever.
            var admits = current == 0 || current + count <= _capacity;
            if (admits && Interlocked.CompareExchange(ref _current, current + count, current) == current)
                return;

            await _signal.WaitAsync(PollInterval, cancellationToken);
        }
    }

    /// <summary>Releases <paramref name="count"/> previously-acquired records back to the gate.</summary>
    public void Release(int count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _current, -count);
        _signal.Release();
    }
}
