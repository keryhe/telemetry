using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Periodic background worker that drains <see cref="MetricTouchTracker"/> and flushes it to the
/// active provider's <see cref="IMetricTouchStore"/> (list-pages-server-side plan, Phase 5,
/// decision 27). Mirrors <see cref="ApiKeyTouchWorker"/>'s shape. Registered unconditionally by
/// <c>AddKeryheTelemetryCollector</c> on every provider except ClickHouse, whose
/// <c>metric_last_seen</c> table is fed by materialized views instead — see
/// <see cref="IMetricTouchStore"/>'s doc comment for why even ClickHouse still needs a (no-op)
/// implementation rather than this worker being conditionally registered.
///
/// Each drained batch is sorted by metric id and capped at
/// <see cref="MetricTouchOptions.MaxBatchSize"/> rows per <see cref="IMetricTouchStore.TouchAsync"/>
/// call (decision 27's deadlock-avoidance rules: concurrent collector instances then always lock
/// rows in the same order, and no single batch is large enough to escalate to a table lock on SQL
/// Server).
/// </summary>
public sealed class MetricTouchWorker(
    IServiceScopeFactory scopeFactory,
    MetricTouchTracker tracker,
    IOptions<MetricTouchOptions> options,
    ILogger<MetricTouchWorker> logger) : BackgroundService
{
    private readonly MetricTouchOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);
        logger.LogInformation("Metric touch worker started (interval: {Interval}s).", _options.FlushIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var touches = tracker.Drain();
                if (touches.Count > 0)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var store = scope.ServiceProvider.GetRequiredService<IMetricTouchStore>();

                    // Sorted once, up front, then sliced into capped batches — decision 27's
                    // deadlock-avoidance rules (consistent lock order across concurrent instances,
                    // no batch large enough to escalate a lock on SQL Server).
                    var sorted = touches.OrderBy(kv => kv.Key).ToList();
                    var batchSize = Math.Max(1, _options.MaxBatchSize);
                    for (var offset = 0; offset < sorted.Count; offset += batchSize)
                    {
                        var batch = sorted.Skip(offset).Take(batchSize).ToList();
                        await store.TouchAsync(batch, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error flushing metric_last_seen — will retry next interval");
                // Drained touches are not re-queued on failure: the field is already best-effort
                // (decision 27's "seen in range" approximation), and a metric still receiving data
                // gets touched again on its next flush and picked up on the next cycle.
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Metric touch worker stopped.");
    }
}
