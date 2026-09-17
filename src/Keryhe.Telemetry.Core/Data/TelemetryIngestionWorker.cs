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
/// </summary>
public sealed class TelemetryIngestionWorker(
    ITelemetryBulkWriter writer,
    TelemetryIngestionChannel ingestionChannel,
    IOptions<TelemetryIngestionOptions> options,
    IngestionMetrics metrics,
    ILogger<TelemetryIngestionWorker> logger) : BackgroundService
{
    private readonly TelemetryIngestionOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                ingestionChannel.LogGate, logDrainLock, "logs", stoppingToken));
            tasks.Add(ProcessChannelAsync(
                ingestionChannel.Traces.Reader, writer.FlushTracesAsync,
                static items => items.Count, _options.MaxTraceFlushSpanBatchSize,
                ingestionChannel.TraceGate, traceDrainLock, "traces", stoppingToken));
            tasks.Add(ProcessChannelAsync(
                ingestionChannel.Metrics.Reader, writer.FlushMetricsAsync,
                static items => items.Count, _options.MaxMetricFlushBatchSize,
                ingestionChannel.MetricGate, metricDrainLock, "metrics", stoppingToken));
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
        string signalName,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            List<T>? batch = null;
            var batchSize = 0;
            try
            {
                if (!await reader.WaitToReadAsync(ct)) break;

                // Serialized across this signal's concurrent loops -- see the class doc comment
                // for why the peek/read pair below cannot safely interleave across loops.
                await drainLock.WaitAsync(ct);
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

                if (batch.Count > 0)
                {
                    try
                    {
                        var flushed = await FlushWithRetryAsync(flush, batch, signalName, ct);
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
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Reaching here means the drain machinery itself faulted (not the flush, which
                // FlushWithRetryAsync already handles) -- e.g. WaitToReadAsync or the drain lock.
                // batch/batchSize may be partially populated; release what was reserved so the
                // gate does not leak, then back off briefly before the next iteration.
                if (batch is { Count: > 0 })
                    gate.Release(batchSize);
                logger.LogError(ex, "Unexpected error in the {Signal} drain loop", signalName);
                await Task.Delay(100, ct);
            }
        }
    }

    /// <summary>
    /// Retries <paramref name="flush"/> with exponential backoff
    /// (<see cref="TelemetryIngestionOptions.RetryBaseDelayMilliseconds"/>, doubling, capped at
    /// <see cref="TelemetryIngestionOptions.RetryMaxDelayMilliseconds"/>) up to
    /// <see cref="TelemetryIngestionOptions.MaxFlushRetries"/> times. Each retry re-invokes
    /// <paramref name="flush"/> from scratch, which on the four transactional providers is safe
    /// by construction: a failed flush's transaction was rolled back in full (see
    /// <c>PostgreSqlBulkWriter</c> et al.'s class doc comments), so re-running it duplicates
    /// nothing. ClickHouse has no such guarantee -- a retry there can double-insert
    /// <c>span_events</c>/<c>span_links</c> if an earlier attempt partially succeeded; that is
    /// the documented, accepted trade-off in <c>ClickHouseBulkWriter</c>, not a bug introduced
    /// here.
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

                var delayMs = Math.Min(
                    _options.RetryBaseDelayMilliseconds * Math.Pow(2, attempt - 1),
                    _options.RetryMaxDelayMilliseconds);
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
