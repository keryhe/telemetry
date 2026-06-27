using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

/// <summary>
/// Provider-agnostic ingestion worker. Drains the three bounded ingestion channels
/// (logs / traces / metrics) and hands each merged batch to the active
/// <see cref="ITelemetryBulkWriter"/>. The channel-draining loop, batching, and
/// backpressure are identical across providers — only the bulk-flush SQL differs,
/// which lives behind <see cref="ITelemetryBulkWriter"/>.
/// </summary>
public sealed class TelemetryIngestionWorker(
    ITelemetryBulkWriter writer,
    TelemetryIngestionChannel ingestionChannel,
    ILogger<TelemetryIngestionWorker> logger) : BackgroundService
{
    private const int MaxBatchSize = 2000;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            ProcessChannelAsync(ingestionChannel.Logs.Reader,    writer.FlushLogsAsync,    "logs",    stoppingToken),
            ProcessChannelAsync(ingestionChannel.Traces.Reader,  writer.FlushTracesAsync,  "traces",  stoppingToken),
            ProcessChannelAsync(ingestionChannel.Metrics.Reader, writer.FlushMetricsAsync, "metrics", stoppingToken));

    private async Task ProcessChannelAsync<T>(
        ChannelReader<List<T>> reader,
        Func<List<T>, CancellationToken, Task> flush,
        string signalName,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(ct)) break;

                // Drain all currently available batches (up to MaxBatchSize items total)
                // without waiting. At low load this is 1 batch; at high load it merges many.
                var batch = new List<T>(capacity: MaxBatchSize);
                while (batch.Count < MaxBatchSize && reader.TryRead(out var items))
                    batch.AddRange(items);

                if (batch.Count > 0)
                    await flush(batch, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error flushing {Signal} batch — batch dropped", signalName);
                await Task.Delay(100, ct);
            }
        }
    }
}
