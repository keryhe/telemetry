using System.Diagnostics;

namespace Keryhe.Telemetry.Core.Data.Threading;

/// <summary>
/// Async gate that bounds what is resident in an ingestion queue by RECORD count and, optionally, by BYTES, rather than by
/// batch count. <see cref="TelemetryIngestionChannel"/>'s channels are unbounded on their own; a paired gate is what
/// actually applies backpressure, so ingestion memory stays bounded independently of how many records one caller merges
/// into a single write. A bounded-by-batch-count channel cannot do this: a single write can itself carry an arbitrary
/// number of records, entirely at the client's discretion. Counting records alone is not enough either, because a record's
/// size ranges from a few hundred bytes to megabytes depending on its attributes and body: the byte budget (the protobuf
/// size of the request that produced the records, a proxy for the models' memory) bounds the other dimension. A byte
/// capacity of 0 turns that budget off.
///
/// A call whose own <c>count</c> (or <c>bytes</c>) exceeds the gate's capacity is not rejected outright -- it is
/// admitted once the gate is otherwise empty, so one oversized batch cannot deadlock the writer
/// forever. It still blocks every subsequent <see cref="AcquireAsync(int, CancellationToken)"/> until that oversized batch
/// is released.
///
/// Wake-ups are signalled, not counted precisely: <see cref="Release(int, long)"/> pulses at most one waiter,
/// so under concurrent contention a released waiter can "steal" another's wake-up.
/// Acquire re-checks on a short timeout regardless of whether it was signalled,
/// so a stolen pulse costs at most that timeout in extra latency, never a permanent stall.
/// </summary>
public sealed class RecordCountGate
{
    /// <summary>Why an acquire did not succeed (or that it did).</summary>
    public enum Result
    {
        Admitted,
        /// <summary>The queue as a whole stayed full for the whole wait.</summary>
        Full,
        /// <summary>The tenant already holds its share of the queue and stayed over it for the whole wait.</summary>
        TenantQuota
    }

    /// <summary>Records and bytes by tenant, built by a worker for what it is releasing and handed to <see cref="Release(TenantTally)"/>.</summary>
    public sealed class TenantTally
    {
        internal readonly Dictionary<long, (int Records, long Bytes)> ByTenant = [];
        public int Records { get; private set; }
        public long Bytes { get; private set; }

        public void Add(long tenantId, int records, long bytes)
        {
            ByTenant.TryGetValue(tenantId, out var cur);
            ByTenant[tenantId] = (cur.Records + records, cur.Bytes + bytes);
            Records += records;
            Bytes += bytes;
        }
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly int _capacity;
    private readonly long _byteCapacity;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly IngestionMetrics? _metrics;
    private readonly string _signalTag;
    private readonly object _sync = new();
    private int _current;
    private long _currentBytes;
    private readonly Dictionary<long, (int Records, long Bytes)> _tenants = [];   // guarded by _sync
    private long _saturatedSince; // Stopwatch timestamp of the first refusal since the gate last had room; 0 = none

    /// <param name="capacity">Resident records admitted at once.</param>
    /// <param name="metrics">When supplied, each acquire records its wait on <c>gate_wait</c>.</param>
    /// <param name="signal">The <c>signal</c> tag for that measurement.</param>
    /// <param name="byteCapacity">Resident bytes admitted at once; 0 = no byte budget.</param>
    public RecordCountGate(int capacity, IngestionMetrics? metrics = null, string signal = "", long byteCapacity = 0)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (byteCapacity < 0) throw new ArgumentOutOfRangeException(nameof(byteCapacity));
        _capacity = capacity;
        _byteCapacity = byteCapacity;
        _metrics = metrics;
        _signalTag = signal;
    }

    /// <summary>Records currently reserved (acquired and not yet released).</summary>
    public int Resident => Volatile.Read(ref _current);

    /// <summary>Bytes currently reserved.</summary>
    public long ResidentBytes => Volatile.Read(ref _currentBytes);

    /// <summary>The most records the gate admits at once (an oversized batch into an empty gate excepted).</summary>
    public int Capacity => _capacity;

    /// <summary>The byte budget, or 0 when there is none.</summary>
    public long ByteCapacity => _byteCapacity;

    /// <summary>True when the resident records or bytes are at or over capacity, so a further request would have to wait.</summary>
    public bool IsSaturated =>
        Volatile.Read(ref _current) >= _capacity || (_byteCapacity > 0 && Volatile.Read(ref _currentBytes) >= _byteCapacity);

    /// <summary>
    /// How long the gate has been continuously refusing admission (zero when it has room, or when no caller has been
    /// refused since it last drained below capacity). Readiness uses it: a gate that is full for a moment is normal
    /// backpressure, one that stays full means the instance is not keeping up.
    /// </summary>
    public TimeSpan SaturatedFor
    {
        get
        {
            var since = Interlocked.Read(ref _saturatedSince);
            return since == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(since);
        }
    }

    /// <summary>Blocks until <paramref name="count"/> records of headroom are available, then reserves them.</summary>
    public Task AcquireAsync(int count, CancellationToken cancellationToken) =>
        AcquireCoreAsync(null, 1.0, count, 0, Infinite, cancellationToken);

    /// <summary>Blocks until <paramref name="count"/> records and <paramref name="bytes"/> of headroom are available, then reserves them.</summary>
    public Task AcquireAsync(int count, long bytes, CancellationToken cancellationToken) =>
        AcquireCoreAsync(null, 1.0, count, bytes, Infinite, cancellationToken);

    /// <summary>
    /// Like <see cref="AcquireAsync(int, CancellationToken)"/> but gives up after <paramref name="maxWait"/>, returning false with nothing
    /// reserved. <see cref="TimeSpan.Zero"/> refuses at once when the gate is full; a negative or infinite span waits
    /// without limit. The "admit when empty" rule still applies, so an oversized batch is never refused for size alone.
    /// </summary>
    public Task<bool> TryAcquireAsync(int count, TimeSpan maxWait, CancellationToken cancellationToken) =>
        TryAcquireAsync(count, 0, maxWait, cancellationToken);

    /// <inheritdoc cref="TryAcquireAsync(int, TimeSpan, CancellationToken)"/>
    public async Task<bool> TryAcquireAsync(int count, long bytes, TimeSpan maxWait, CancellationToken cancellationToken) =>
        await AcquireCoreAsync(null, 1.0, count, bytes, NormalizeWait(maxWait), cancellationToken) == Result.Admitted;

    /// <summary>
    /// Reserves for a tenant: besides the whole-queue check, <paramref name="tenantId"/> may not hold more than
    /// <paramref name="maxShare"/> of the record capacity (nor of the byte capacity), unless it holds nothing at all (an export larger
    /// than its share is not refused for size alone). A share of 1 or more is no quota. Release with <see cref="Release(TenantTally)"/>.
    /// </summary>
    public Task<Result> TryAcquireAsync(long tenantId, double maxShare, int count, long bytes, TimeSpan maxWait, CancellationToken cancellationToken) =>
        AcquireCoreAsync(tenantId, maxShare, count, bytes, NormalizeWait(maxWait), cancellationToken);

    private static readonly TimeSpan Infinite = System.Threading.Timeout.InfiniteTimeSpan;

    private static TimeSpan NormalizeWait(TimeSpan wait) => wait < TimeSpan.Zero ? Infinite : wait;

    private async Task<Result> AcquireCoreAsync(long? tenantId, double maxShare, int count, long bytes, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        if (count <= 0) return Result.Admitted;
        if (_byteCapacity == 0) bytes = 0;

        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            Result result;
            lock (_sync)
            {
                // Admit unconditionally when the gate is empty, even if count or bytes alone exceed capacity
                // (the "one oversized batch" allowance documented on the type) -- otherwise a caller
                // requesting more than the capacity could never be admitted and would wait forever.
                var totalFits = _current == 0
                    || (_current + count <= _capacity && (_byteCapacity == 0 || _currentBytes + bytes <= _byteCapacity));

                var overShare = false;
                (int Records, long Bytes) held = default;
                if (tenantId is { } t && maxShare < 1.0)
                {
                    _tenants.TryGetValue(t, out held);
                    overShare = held.Records > 0
                        && (held.Records + count > maxShare * _capacity
                            || (_byteCapacity > 0 && held.Bytes + bytes > maxShare * _byteCapacity));
                }

                result = overShare ? Result.TenantQuota : totalFits ? Result.Admitted : Result.Full;
                if (result == Result.Admitted)
                {
                    _current += count;
                    _currentBytes += bytes;
                    if (tenantId is { } id)
                    {
                        _tenants.TryGetValue(id, out var cur);
                        _tenants[id] = (cur.Records + count, cur.Bytes + bytes);
                    }
                }
            }
            if (result == Result.Admitted)
            {
                _metrics?.RecordGateWait(_signalTag, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return result;
            }

            // Only a full queue says the instance is not keeping up; one tenant over its share does not make it unready.
            if (result == Result.Full) Interlocked.CompareExchange(ref _saturatedSince, Stopwatch.GetTimestamp(), 0);

            var remaining = maxWait == Infinite ? PollInterval : maxWait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                _metrics?.RecordGateWait(_signalTag, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return result;
            }
            await _signal.WaitAsync(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
        }
    }

    /// <summary>Records currently held per tenant, for the <c>tenant_resident_records</c> gauge.</summary>
    public IReadOnlyDictionary<long, int> TenantResident()
    {
        lock (_sync) return _tenants.Where(kv => kv.Value.Records > 0).ToDictionary(kv => kv.Key, kv => kv.Value.Records);
    }

    /// <summary>Releases <paramref name="count"/> previously-acquired records back to the gate.</summary>
    public void Release(int count) => Release(count, 0);

    /// <summary>Releases what a worker has finished with, per tenant, so each tenant's share is given back.</summary>
    public void Release(TenantTally tally)
    {
        if (tally.Records <= 0 && tally.Bytes <= 0) return;
        lock (_sync)
        {
            foreach (var (tenant, amount) in tally.ByTenant)
            {
                if (!_tenants.TryGetValue(tenant, out var cur)) continue;
                var records = cur.Records - amount.Records;
                var bytes = cur.Bytes - amount.Bytes;
                if (records <= 0 && bytes <= 0) _tenants.Remove(tenant);
                else _tenants[tenant] = (Math.Max(0, records), Math.Max(0, bytes));
            }
            ReleaseTotals(tally.Records, tally.Bytes);
        }
        _signal.Release();
    }

    private void ReleaseTotals(int count, long bytes)
    {
        _current -= Math.Max(0, count);
        _currentBytes = Math.Max(0, _currentBytes - Math.Max(0, bytes));
        var hasRoom = _current < _capacity && (_byteCapacity == 0 || _currentBytes < _byteCapacity);
        if (hasRoom) Interlocked.Exchange(ref _saturatedSince, 0);
    }

    /// <summary>Releases <paramref name="count"/> records and <paramref name="bytes"/> previously acquired (without a tenant).</summary>
    public void Release(int count, long bytes)
    {
        if (count <= 0 && bytes <= 0) return;
        lock (_sync) ReleaseTotals(count, bytes);
        _signal.Release();
    }
}
