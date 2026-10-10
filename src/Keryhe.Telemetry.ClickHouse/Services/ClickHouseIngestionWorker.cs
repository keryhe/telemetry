using System.Diagnostics;
using System.Threading.Channels;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Threading;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// The token overloads of <see cref="ClickHouseBulkWriter"/>, as a seam so the worker can be tested without a database.
/// </summary>
internal interface IClickHouseTokenWriter
{
    Task FlushLogsAsync(List<LogRecordModel> records, string token, CancellationToken ct = default);
    Task FlushTracesAsync(List<SpanModel> spans, string token, CancellationToken ct = default);
    Task FlushMetricsAsync(List<MetricModel> metrics, string token, CancellationToken ct = default);
}

/// <summary>
/// ClickHouse's ingestion worker (plans/clickhouse-redesign phase 2), replacing the shared
/// <c>TelemetryIngestionWorker</c> for this provider. It drains the same <see cref="TelemetryIngestionChannel"/> and
/// releases the same gates, but batches for ClickHouse: few, large inserts.
///
/// One loop per signal sorts drained records into <b>day buffers</b> keyed by the UTC day of the record's partition
/// time. The current day's buffer flushes when it holds <c>MaxBatchRecords</c> or has lingered
/// <see cref="ClickHouseIngestionOptions.LingerMilliseconds"/> since its first record; an earlier day's buffer waits
/// <see cref="ClickHouseIngestionOptions.LateLingerMilliseconds"/> instead (late data arrives in trickles, and one part
/// per day per flush is what ClickHouse should not be asked for). Every flush is one insert per day per table with a
/// token minted when the buffer is sealed and reused on each retry, so a retried batch is stored once. A buffer is
/// never re-batched after sealing; an oversize one is cut into pieces that each get a new token.
///
/// The gate is released for a record when its flush finishes (success or drop), so backpressure covers records
/// waiting in a buffer. A record older than the retention window is dropped before buffering. Shutdown drains like the
/// shared worker: writers complete, every buffer flushes regardless of linger, and what is left at the host's
/// deadline is counted as dropped.
/// </summary>
internal sealed class ClickHouseIngestionWorker(
    IClickHouseTokenWriter writer,
    TelemetryIngestionChannel channel,
    IOptions<ClickHouseIngestionOptions> options,
    IOptions<TelemetryIngestionOptions> sharedOptions,
    IngestionMetrics metrics,
    IRetentionWindows retention,
    TimeProvider time,
    ILogger<ClickHouseIngestionWorker> logger,
    IFlushErrorClassifier? classifier = null) : BackgroundService
{
    private readonly IFlushErrorClassifier _classifier = classifier ?? DefaultFlushErrorClassifier.Instance;

    private static readonly TimeSpan AbortGracePeriod = TimeSpan.FromSeconds(2);

    // Fields (not just primary-constructor parameters) so the nested pumps can reach them.
    private readonly IngestionMetrics metrics = metrics;
    private readonly TelemetryIngestionChannel channel = channel;
    private readonly IRetentionWindows retention = retention;
    private readonly TimeProvider time = time;
    private readonly ILogger<ClickHouseIngestionWorker> logger = logger;
    private readonly ClickHouseIngestionOptions _options = options.Value;
    private readonly TelemetryIngestionOptions _shared = sharedOptions.Value;
    private readonly CancellationTokenSource _abort = new();
    private readonly List<IPump> _pumps = [];

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        channel.CompleteWriters();
        await base.StopAsync(cancellationToken);

        if (ExecuteTask is { IsCompleted: false } executeTask)
        {
            _ = _abort.CancelAsync();
            await executeTask.WaitAsync(AbortGracePeriod).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        foreach (var pump in _pumps) pump.ReportUnpersisted();
    }

    public override void Dispose()
    {
        base.Dispose();
        _abort.Dispose();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Room in the gate appears a whole day buffer at a time, about every linger plus flush time, so a bounded wait
        // shorter than the linger refuses exports the next flush would have admitted.
        if (_shared.MaxGateWaitMilliseconds is { } wait && wait >= 0 && wait < _options.LingerMilliseconds)
            logger.LogWarning(
                "Telemetry:Ingestion:MaxGateWaitMilliseconds ({Wait} ms) is below Telemetry:ClickHouse:Ingestion:LingerMilliseconds ({Linger} ms): " +
                "a full queue frees room only when a buffer flushes, so exports will be refused that a short wait would have admitted",
                wait, _options.LingerMilliseconds);

        var abort = _abort.Token;
        _pumps.Add(new Pump<LogRecordModel>(this, "logs", channel.Logs.Reader, channel.LogGate, _options.MaxLogBatchRecords,
            LogTime, writer.FlushLogsAsync));
        _pumps.Add(new Pump<SpanModel>(this, "traces", channel.Traces.Reader, channel.TraceGate, _options.MaxSpanBatchRecords,
            static s => s.StartTimeUnixNano, writer.FlushTracesAsync));
        _pumps.Add(new Pump<MetricModel>(this, "metrics", channel.Metrics.Reader, channel.MetricGate, _options.MaxMetricBatchRecords,
            MetricTime, writer.FlushMetricsAsync));

        return Task.WhenAll(_pumps.Select(p => p.RunAsync(stoppingToken, abort)).Append(RefreshRetentionAsync(stoppingToken)));
    }

    private async Task RefreshRetentionAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await retention.RefreshAsync(timeout.Token);
            try { await Task.Delay(TimeSpan.FromSeconds(retention.RefreshSeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ---- partition time of a record, in unix nanoseconds ----

    private long NowNano() => time.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;

    private long LogTime(LogRecordModel r)
        => r.TimeUnixNano is > 0 ? r.TimeUnixNano.Value : r.ObservedTimeUnixNano is > 0 ? r.ObservedTimeUnixNano.Value : NowNano();

    // The newest point decides a metric's day and whether retention drops it: one metric holds points of several times
    // (an exporter interval, a late point), and judging it by its first point would drop its current points with an old one.
    // Older points in a kept metric are stored in their own day's partition and age out with it.
    private long MetricTime(MetricModel m)
    {
        var t = m.Type switch
        {
            MetricType.GAUGE => m.GaugeDataPoints?.Max(p => (long?)p.TimeUnixNano),
            MetricType.SUM => m.SumDataPoints?.Max(p => (long?)p.TimeUnixNano),
            MetricType.HISTOGRAM => m.HistogramDataPoints?.Max(p => (long?)p.TimeUnixNano),
            MetricType.EXPONENTIAL_HISTOGRAM => m.ExponentialHistogramDataPoints?.Max(p => (long?)p.TimeUnixNano),
            _ => m.SummaryDataPoints?.Max(p => (long?)p.TimeUnixNano)
        };
        return t is > 0 ? t.Value : NowNano();
    }

    private static DateOnly DayOf(long unixNano)
        => DateOnly.FromDateTime(DateTime.UnixEpoch.AddTicks(Math.Max(0L, unixNano) / 100));

    // =========================================================================
    // One signal's drain loop
    // =========================================================================

    private interface IPump
    {
        Task RunAsync(CancellationToken stopping, CancellationToken abort);
        void ReportUnpersisted();
    }

    private sealed class DayBuffer<T>
    {
        public readonly List<T> Items = [];
        public readonly List<long> Stamps = [];
        public long FirstAt = Stopwatch.GetTimestamp();
        public readonly RecordCountGate.TenantTally Tally = new();   // this buffer's records and share of the exports' gate bytes, by tenant, released with it
    }

    private sealed class Pump<T>(
        ClickHouseIngestionWorker owner,
        string signal,
        ChannelReader<List<T>> reader,
        RecordCountGate gate,
        int maxBatch,
        Func<T, long> timeOf,
        Func<List<T>, string, CancellationToken, Task> flush) : IPump
    {
        private readonly Dictionary<DateOnly, DayBuffer<T>> _buffers = [];
        private int _held;            // records in buffers or in a flush, not yet released to the gate
        private int _late;            // records in non-current day buffers, read by the gauge
        private bool _registered;

        private ClickHouseIngestionOptions Options => owner._options;

        private DateOnly Today => DateOnly.FromDateTime(owner.time.GetUtcNow().UtcDateTime);

        public async Task RunAsync(CancellationToken stopping, CancellationToken abort)
        {
            if (!_registered)
            {
                owner.metrics.RegisterLateBufferRecords(signal, () => Volatile.Read(ref _late));
                _registered = true;
            }

            var completed = false;
            while (true)
            {
                try
                {
                    if (!stopping.IsCancellationRequested)
                    {
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                        if (NextDueIn() is { } due) wait.CancelAfter(due);
                        try
                        {
                            // false = writers completed AND channel empty
                            if (!await reader.WaitToReadAsync(wait.Token)) completed = true;
                        }
                        catch (OperationCanceledException) when (!abort.IsCancellationRequested)
                        {
                            // a buffer is due, or the host is stopping: fall through
                        }
                    }

                    Drain();
                    var draining = stopping.IsCancellationRequested || completed;
                    await FlushDueAsync(force: draining, abort);

                    if (draining && _buffers.Count == 0 && !reader.TryPeek(out _)) break;
                }
                catch (OperationCanceledException) when (abort.IsCancellationRequested)
                {
                    break; // StopAsync reports what is left
                }
                catch (Exception ex)
                {
                    LogLoopError(ex);
                    try { await Task.Delay(100, abort); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private void LogLoopError(Exception ex)
            => owner.logger.LogError(ex, "Unexpected error in the {Signal} drain loop", signal);

        /// <summary>Sorts everything currently queued into day buffers, dropping what is older than retention.</summary>
        private void Drain()
        {
            var oldest = owner.retention.OldestAllowedUtc(signal);
            var oldestNano = oldest is null ? 0L : (oldest.Value - DateTime.UnixEpoch).Ticks * 100;
            var droppedTally = new RecordCountGate.TenantTally();
            var dropped = 0;

            while (reader.TryRead(out var items))
            {
                var stampPending = owner.channel.TryTakeEnqueued(items, out var stamp, out var exportBytes);
                // One export's bytes were reserved as a unit but its records may land in several day buffers (or be dropped),
                // so each record carries an equal share (the remainder on the first) and a buffer releases its records' shares.
                var share = items.Count == 0 ? 0 : exportBytes / items.Count;
                var remainder = exportBytes - share * items.Count;
                var first = true;
                foreach (var item in items)
                {
                    var itemBytes = share + (first ? remainder : 0);
                    first = false;
                    var nano = timeOf(item);
                    if (oldest is not null && nano < oldestNano) { dropped++; droppedTally.Add(TenantOf.Of(item!), 1, itemBytes); continue; }

                    var day = DayOf(nano);
                    if (day > Today) day = Today; // clock skew counts as current
                    if (!_buffers.TryGetValue(day, out var buffer))
                        _buffers[day] = buffer = new DayBuffer<T>();
                    buffer.Items.Add(item);
                    buffer.Tally.Add(TenantOf.Of(item!), 1, itemBytes);
                    Interlocked.Increment(ref _held);
                    if (stampPending) { buffer.Stamps.Add(stamp); stampPending = false; }
                }
            }

            if (dropped > 0)
            {
                owner.metrics.RecordDropped(signal, dropped, "out_of_retention");
                gate.Release(droppedTally);
            }
        }

        private TimeSpan? NextDueIn()
        {
            if (_buffers.Count == 0) return null;
            var today = Today;
            var now = Stopwatch.GetTimestamp();
            long best = long.MaxValue;
            foreach (var (day, buffer) in _buffers)
            {
                var linger = day >= today ? Options.LingerMilliseconds : Options.LateLingerMilliseconds;
                var remaining = buffer.FirstAt + Stopwatch.Frequency * linger / 1000 - now;
                best = Math.Min(best, remaining);
            }
            return best <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)best / Stopwatch.Frequency);
        }

        private async Task FlushDueAsync(bool force, CancellationToken abort)
        {
            var today = Today;
            var now = Stopwatch.GetTimestamp();

            bool Due(DateOnly day, DayBuffer<T> b)
            {
                if (force || b.Items.Count >= maxBatch) return true;
                var linger = day >= today ? Options.LingerMilliseconds : Options.LateLingerMilliseconds;
                return now - b.FirstAt >= Stopwatch.Frequency * linger / 1000;
            }

            // current day first, then late days oldest first
            var order = _buffers.Keys.OrderByDescending(d => d >= today).ThenBy(d => d).ToList();
            var flushNow = order.Where(d => Due(d, _buffers[d])).ToList();

            // too many late buffers: the oldest go early
            var lateWaiting = order.Where(d => d < today && !flushNow.Contains(d)).ToList();
            for (var i = 0; i < lateWaiting.Count - Options.MaxLateDayBuffers; i++)
                flushNow.Add(lateWaiting[i]);

            foreach (var day in flushNow)
            {
                var buffer = _buffers[day];
                _buffers.Remove(day);
                await FlushBufferAsync(buffer, abort);
            }

            Volatile.Write(ref _late, _buffers.Where(kv => kv.Key < today).Sum(kv => kv.Value.Items.Count));
        }

        private async Task FlushBufferAsync(DayBuffer<T> buffer, CancellationToken abort)
        {
            var total = buffer.Items.Count;
            var allOk = true;
            {
                for (var offset = 0; offset < total; offset += maxBatch)
                {
                    var piece = offset == 0 && total <= maxBatch ? buffer.Items : buffer.Items.GetRange(offset, Math.Min(maxBatch, total - offset));
                    owner.metrics.RecordFlushBatchSize(signal, piece.Count);
                    var outcome = await owner.FlushWithRetryAsync(flush, piece, signal, abort);
                    if (outcome == FlushOutcome.Ok)
                        owner.metrics.RecordFlushed(signal, piece.Count);
                    else if (outcome == FlushOutcome.Exhausted)
                    {
                        allOk = false;
                        owner.metrics.RecordDropped(signal, piece.Count, "retries_exhausted");
                    }
                    else
                    {
                        // The database refuses something in this piece: bisect to the offending records. Every half is a different
                        // row set, so each attempt mints its own token (ClickHouse would silently discard a different block sent
                        // under a token it has already seen). Pieces of the failed flush that had already landed are stored again.
                        var split = await BatchBisector.RunAsync(piece,
                            (half, token) => owner.FlushWithRetryAsync(flush, half, signal, token), owner._shared.MaxSplitFlushes, abort);
                        foreach (var stored in split.Flushed) owner.metrics.RecordFlushed(signal, stored.Count);
                        foreach (var bad in split.Permanent.Take(10))
                            owner.logger.LogWarning("Dropped a {Signal} record ClickHouse refuses: {Record}", signal, DroppedRecordDescription.Of(bad!));
                        owner.logger.LogWarning(
                            "Split a {Signal} piece of {Count} after a permanent flush error with {Flushes} extra flushes: {Stored} stored, " +
                            "{Permanent} refused, {Exhausted} lost to exhausted retries, {Cap} dropped at the split cap",
                            signal, piece.Count, split.Flushes, split.Flushed.Sum(p => p.Count), split.Permanent.Count, split.Exhausted, split.CapDropped);
                        owner.metrics.RecordDropped(signal, split.Permanent.Count, "permanent");
                        owner.metrics.RecordDropped(signal, split.Exhausted, "retries_exhausted");
                        owner.metrics.RecordDropped(signal, split.CapDropped, "split_cap");
                        if (split.Permanent.Count + split.Exhausted + split.CapDropped > 0) allOk = false;
                    }
                }

                if (allOk)
                {
                    var committed = Stopwatch.GetTimestamp();
                    foreach (var stamp in buffer.Stamps)
                        owner.metrics.RecordCommitLag(signal, Stopwatch.GetElapsedTime(stamp, committed).TotalMilliseconds);
                }
            }
            // Released regardless of outcome: a dropped batch still leaves the queue. A flush cut off by the shutdown
            // deadline throws before this line, so its records stay held for StopAsync to report.
            gate.Release(buffer.Tally);
            Interlocked.Add(ref _held, -total);
        }

        public void ReportUnpersisted()
        {
            var count = Volatile.Read(ref _held);
            while (reader.TryRead(out var items)) count += items.Count;
            if (count == 0) return;
            owner.metrics.RecordDropped(signal, count, "shutdown");
            owner.logger.LogWarning(
                "Shutdown deadline reached before the {Signal} queue drained -- {Count} records were not persisted", signal, count);
        }
    }

    // =========================================================================
    // Retry, reusing the shared options (MaxFlushRetries, backoff)
    // =========================================================================

    /// <summary>
    /// Flushes <paramref name="batch"/> under one deduplication token, minted here and reused on every retry (a retried
    /// insert is stored once), retrying transient errors with the shared backoff. A permanent error (per the provider's
    /// classifier) is not retried: the caller splits the batch, and each piece comes back through here with a new token.
    /// </summary>
    private async Task<FlushOutcome> FlushWithRetryAsync<T>(
        Func<List<T>, string, CancellationToken, Task> flush, List<T> batch, string signal, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        var attempt = 0;
        while (true)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await flush(batch, token, ct);
                metrics.RecordFlushDuration(signal, Stopwatch.GetElapsedTime(started).TotalMilliseconds, "ok");
                return FlushOutcome.Ok;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                metrics.RecordFlushDuration(signal, Stopwatch.GetElapsedTime(started).TotalMilliseconds, "failed");
                if (_classifier.Classify(ex) == FlushErrorKind.Permanent)
                {
                    logger.LogWarning(ex, "Permanent error flushing {Signal} batch of {Count} -- not retrying", signal, batch.Count);
                    return FlushOutcome.Permanent;
                }
                attempt++;
                if (attempt > _shared.MaxFlushRetries)
                {
                    logger.LogError(ex, "Error flushing {Signal} batch of {Count} after {Attempts} attempts -- batch dropped", signal, batch.Count, attempt);
                    return FlushOutcome.Exhausted;
                }

                metrics.RecordFlushRetry(signal);
                var backoffMs = Math.Min(_shared.RetryBaseDelayMilliseconds * Math.Pow(2, attempt - 1), _shared.RetryMaxDelayMilliseconds);
                var delayMs = backoffMs * (0.5 + Random.Shared.NextDouble() * 0.5);
                logger.LogWarning(ex, "Error flushing {Signal} batch of {Count} (attempt {Attempt}/{MaxAttempts}) -- retrying in {DelayMs}ms",
                    signal, batch.Count, attempt, _shared.MaxFlushRetries + 1, delayMs);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
            }
        }
    }
}
