using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Threading;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Provider-agnostic ingestion worker. Drains the three ingestion channels (logs / traces /
/// metrics) and hands each merged batch to the active <see cref="ITelemetryBulkWriter"/>. The
/// channel-draining loop, batching, retry, and backpressure are identical across providers —
/// only the bulk-flush SQL differs, which lives behind <see cref="ITelemetryBulkWriter"/>.
///
/// Batch size is measured in RECORDS for logs/metrics and in SPANS for traces (not trace count) —
/// see <see cref="TelemetryIngestionOptions"/>. After each drained batch is flushed (successfully,
/// after retry, or dropped once retries are exhausted), the worker releases the same count back
/// to the channel's <see cref="RecordCountGate"/> so producer backpressure tracks what actually
/// left the queue.
///
/// Runs <see cref="TelemetryIngestionOptions.FlushConcurrency"/> concurrent loops per signal
/// against the SAME channel (<c>SingleReader = false</c>), so one signal's DB write latency
/// overlaps across loops instead of every flush for that signal serializing behind the last. The
/// in-memory step of picking the next batch off the channel is still serialized per signal, via
/// an internal <see cref="SemaphoreSlim"/> — that step is cheap, in-process work and was never the
/// bottleneck; letting multiple loops interleave <c>TryPeek</c>/<c>TryRead</c> on one channel
/// unsynchronized would let loop A peek an item, loop B read it away, and loop A then read a
/// DIFFERENT item while still holding A's now-stale size for the first — corrupting the very
/// batch-size accounting <see cref="RecordCountGate.Release"/> depends on. Only the flush itself
/// (the actual DB round trip, including retries) runs fully concurrently across loops.
///
/// <b>Shutdown drains, it does not abandon.</b> Every record in the channels has already been
/// acknowledged to its OTLP client as accepted, so stopping the moment the host signals shutdown
/// would silently lose up to <c>MaxQueued*</c> records per signal on every restart, redeploy or
/// scale-in. Instead <see cref="StopAsync"/> completes the channel writers (a late export then
/// fails with gRPC <c>UNAVAILABLE</c>, which OTLP clients retry — against another instance, behind
/// a load balancer) and the loops keep flushing until the channels are empty. Two tokens drive
/// this: the host's <c>stoppingToken</c> only means "stop waiting for new work", while an internal
/// abort token — cancelled when the host's own shutdown deadline (<c>HostOptions.ShutdownTimeout</c>,
/// 30s by default, shared with Kestrel's request drain) expires — is what flushes and retries
/// honour. Whatever is still unflushed when that deadline hits is counted on
/// <see cref="IngestionMetrics"/>'s <c>records_dropped</c> and logged, never lost silently.
/// </summary>
public sealed class TelemetryIngestionWorker(
    ITelemetryBulkWriter writer,
    TelemetryIngestionChannel ingestionChannel,
    IOptions<TelemetryIngestionOptions> options,
    IngestionMetrics metrics,
    ILogger<TelemetryIngestionWorker> logger) : BackgroundService
{
    private readonly TelemetryIngestionOptions _options = options.Value;

    // Cancelled only when the host's shutdown deadline expires -- see the class doc comment.
    private readonly CancellationTokenSource _abort = new();

    // How long StopAsync waits, once the deadline has hit, for the loops to observe the abort
    // before it reports what they left unpersisted. Deliberately short: this runs past the host's
    // own deadline, and a loop blocked in a driver call that ignores cancellation (Npgsql's Open,
    // against an unresponsive server, waits out its own connect timeout) may never return in time
    // anyway -- which is why the reporting below does not depend on the loops at all.
    private static readonly TimeSpan AbortGracePeriod = TimeSpan.FromSeconds(2);

    // Records handed to a flush that has not yet come back with an outcome, per signal. A flush
    // cut off by the shutdown deadline is deliberately left counted here -- see ReportUnpersisted.
    private readonly InFlightCount _logsInFlight = new();
    private readonly InFlightCount _tracesInFlight = new();
    private readonly InFlightCount _metricsInFlight = new();

    private sealed class InFlightCount { public int Value; }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Completing the writers first means no enqueue can land after the drain loops have
        // decided the channel is empty and exited.
        ingestionChannel.CompleteWriters();
        await base.StopAsync(cancellationToken);

        // Still running here means the deadline hit: base.StopAsync returns the moment it does,
        // without waiting for the loops. CancelAsync, deliberately not awaited, rather than Cancel:
        // Cancel runs the token's callbacks synchronously on this thread, and a DB driver's
        // callback can itself block -- Npgsql's sends a cancel request to the very server that
        // stopped responding, stalling shutdown for its full connect timeout.
        if (ExecuteTask is { IsCompleted: false } executeTask)
        {
            _ = _abort.CancelAsync();
            await executeTask.WaitAsync(AbortGracePeriod).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // No-ops after a complete drain, where the queues are empty and nothing is in flight.
        ReportUnpersisted("logs", ingestionChannel.Logs.Reader, _logsInFlight);
        ReportUnpersisted("traces", ingestionChannel.Traces.Reader, _tracesInFlight);
        ReportUnpersisted("metrics", ingestionChannel.Metrics.Reader, _metricsInFlight);
    }

    /// <summary>
    /// Shutdown deadline reached: counts what is still queued for this signal plus what was cut
    /// off mid-flush onto <c>records_dropped</c>, and logs it, so the loss is visible rather than
    /// silent. Done here rather than in the loops because a loop may still be blocked in an
    /// uncancellable driver call when the process exits. Errs toward over-reporting: a flush that
    /// was still blocked at this point but completes in the moments before exit is counted anyway.
    /// </summary>
    private void ReportUnpersisted<T>(string signalName, ChannelReader<List<T>> reader, InFlightCount inFlight)
    {
        var count = Volatile.Read(ref inFlight.Value);
        while (reader.TryRead(out var items))
            count += items.Count;
        if (count == 0) return;

        metrics.RecordDropped(signalName, count);
        logger.LogWarning(
            "Shutdown deadline reached before the {Signal} queue drained -- {Count} records were not persisted",
            signalName, count);
    }

    public override void Dispose()
    {
        base.Dispose();
        _abort.Dispose();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Captured once: the loops can outlive StopAsync when the deadline hits, and reading
        // Token off an already-disposed source throws, whereas a captured token stays readable.
        var abortToken = _abort.Token;
        var concurrency = Math.Max(1, _options.FlushConcurrency);
        var logDrainLock = new SemaphoreSlim(1, 1);
        var traceDrainLock = new SemaphoreSlim(1, 1);
        var metricDrainLock = new SemaphoreSlim(1, 1);

        var tasks = new List<Task>(concurrency * 3);
        for (var i = 0; i < concurrency; i++)
        {
            tasks.Add(ProcessChannelAsync(
                ingestionChannel.Logs.Reader, writer.FlushLogsAsync,
                static items => items.Count, _options.MaxLogFlushBatchSize,
                ingestionChannel.LogGate, logDrainLock, _logsInFlight, "logs", stoppingToken, abortToken));
            tasks.Add(ProcessChannelAsync(
                ingestionChannel.Traces.Reader, writer.FlushTracesAsync,
                static items => items.Count, _options.MaxTraceFlushSpanBatchSize,
                ingestionChannel.TraceGate, traceDrainLock, _tracesInFlight, "traces", stoppingToken, abortToken));
            tasks.Add(ProcessChannelAsync(
                ingestionChannel.Metrics.Reader, writer.FlushMetricsAsync,
                static items => items.Count, _options.MaxMetricFlushBatchSize,
                ingestionChannel.MetricGate, metricDrainLock, _metricsInFlight, "metrics", stoppingToken, abortToken));
        }

        return Task.WhenAll(tasks);
    }

    private async Task ProcessChannelAsync<T>(
        ChannelReader<List<T>> reader,
        Func<List<T>, CancellationToken, Task> flush,
        Func<List<T>, int> sizeOf,
        int maxBatchSize,
        RecordCountGate gate,
        SemaphoreSlim drainLock,
        InFlightCount inFlight,
        string signalName,
        CancellationToken stoppingToken,
        CancellationToken abortToken)
    {
        while (true)
        {
            List<T>? batch = null;
            var batchSize = 0;
            try
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        // false = writers completed AND channel empty: nothing left to drain.
                        if (!await reader.WaitToReadAsync(stoppingToken)) break;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Shutdown: stop waiting for new work, fall through and drain what is queued.
                    }
                }

                // Serialized across this signal's concurrent loops -- see the class doc comment
                // for why the peek/read pair below cannot safely interleave across loops.
                await drainLock.WaitAsync(abortToken);
                try
                {
                    // Drain all currently available writes (up to maxBatchSize units — records
                    // for logs/metrics, spans for traces) without waiting. Peek before read: an
                    // item is only merged in when it fits the remaining headroom, so the batch
                    // cannot overshoot maxBatchSize the way a plain "read then check" loop can —
                    // except for a single oversized item taken alone, the same one-off allowance
                    // RecordCountGate makes for its own capacity. At low load this drains one
                    // write; at high load it merges many.
                    batch = new List<T>();
                    while (reader.TryPeek(out var peeked))
                    {
                        var peekedSize = sizeOf(peeked);
                        if (batch.Count > 0 && batchSize + peekedSize > maxBatchSize)
                            break;
                        if (!reader.TryRead(out var items))
                            break;

                        batch.AddRange(items);
                        batchSize += peekedSize;
                        if (batchSize >= maxBatchSize)
                            break;
                    }
                }
                finally
                {
                    drainLock.Release();
                }

                if (batch.Count == 0)
                {
                    // Another loop took what WaitToReadAsync saw -- normal while running; while
                    // stopping it means the channel is drained.
                    if (stoppingToken.IsCancellationRequested) break;
                    continue;
                }

                Interlocked.Add(ref inFlight.Value, batchSize);
                try
                {
                    var flushed = await FlushWithRetryAsync(flush, batch, signalName, abortToken);
                    // Not in the finally: a flush cut off by the deadline (the only way
                    // FlushWithRetryAsync throws) stays counted for StopAsync to report.
                    Interlocked.Add(ref inFlight.Value, -batchSize);
                    if (!flushed)
                        metrics.RecordDropped(signalName, batchSize);
                }
                finally
                {
                    // Release regardless of outcome: a dropped batch still leaves the queue,
                    // and a gate that only releases on success leaks capacity on every error
                    // until ingestion deadlocks permanently.
                    gate.Release(batchSize);
                }
            }
            catch (OperationCanceledException) when (abortToken.IsCancellationRequested)
            {
                // The host's shutdown deadline expired mid-drain; StopAsync reports what is left.
                break;
            }
            catch (Exception ex)
            {
                // Reaching here means the drain machinery itself faulted (not the flush, which
                // FlushWithRetryAsync already handles) -- e.g. WaitToReadAsync or the drain lock.
                // batch/batchSize may be partially populated; release what was reserved so the
                // gate does not leak, then back off briefly before the next iteration.
                if (batch is { Count: > 0 })
                    gate.Release(batchSize);
                logger.LogError(ex, "Unexpected error in the {Signal} drain loop", signalName);
                try
                {
                    await Task.Delay(100, abortToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Retries <paramref name="flush"/> with exponential backoff plus full jitter
    /// (<see cref="TelemetryIngestionOptions.RetryBaseDelayMilliseconds"/>, doubling, capped at
    /// <see cref="TelemetryIngestionOptions.RetryMaxDelayMilliseconds"/>, then randomized down to
    /// somewhere in [50%, 100%] of that value) up to
    /// <see cref="TelemetryIngestionOptions.MaxFlushRetries"/> times. The jitter matters here
    /// specifically because retries are not independent: a transient failure that hits one flush
    /// (a deadlock between two concurrent upserts, a brief DB blip) often hits several at once,
    /// and fixed backoff would have every one of them retry at exactly the same moment, with good
    /// odds of colliding again. Each retry re-invokes
    /// <paramref name="flush"/> from scratch, which on the four transactional providers is safe
    /// by construction: a failed flush's transaction was rolled back in full (see
    /// <c>PostgreSqlBulkWriter</c> et al.'s class doc comments), so re-running it duplicates
    /// nothing. ClickHouse has no transaction to roll back, but a retried trace flush is
    /// nonetheless idempotent since schema 2.11.0: spans is a <c>ReplacingMergeTree</c> keyed on
    /// (trace_id, span_id) with app-derived ids, and a span's events and links now ride along as
    /// JSON columns on that same row rather than as separate <c>span_events</c>/<c>span_links</c>
    /// inserts, which were the one part of a trace flush a retry could genuinely duplicate.
    ///
    /// Returns <c>true</c> if the flush eventually succeeded, <c>false</c> if every attempt
    /// failed and the batch is being dropped.
    /// </summary>
    private async Task<bool> FlushWithRetryAsync<T>(
        Func<List<T>, CancellationToken, Task> flush,
        List<T> batch,
        string signalName,
        CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await flush(batch, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                if (attempt > _options.MaxFlushRetries)
                {
                    logger.LogError(ex,
                        "Error flushing {Signal} batch of {Count} after {Attempts} attempts — batch dropped",
                        signalName, batch.Count, attempt);
                    return false;
                }

                var backoffMs = Math.Min(
                    _options.RetryBaseDelayMilliseconds * Math.Pow(2, attempt - 1),
                    _options.RetryMaxDelayMilliseconds);
                // Full jitter within the top half of the window: enough randomization to
                // decorrelate simultaneous retries, without also reintroducing the "retry
                // instantly and collide again" case a jitter down to zero would allow back in.
                var delayMs = backoffMs * (0.5 + Random.Shared.NextDouble() * 0.5);
                logger.LogWarning(ex,
                    "Error flushing {Signal} batch of {Count} (attempt {Attempt}/{MaxAttempts}) — retrying in {DelayMs}ms",
                    signalName, batch.Count, attempt, _options.MaxFlushRetries + 1, delayMs);

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
            }
        }
    }
}
