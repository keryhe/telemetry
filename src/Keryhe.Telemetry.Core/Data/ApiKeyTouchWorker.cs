using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Periodic background worker that drains <see cref="ApiKeyTouchTracker"/> and flushes it to the
/// active provider's <see cref="IApiKeyTouchStore"/>. Runs unconditionally on all five providers —
/// there is no per-provider flag here, because ClickHouse opting out is expressed by its
/// <see cref="IApiKeyTouchStore"/> implementation being a no-op, not by this worker knowing which
/// provider is active. A scope is created per cycle because the store is registered scoped,
/// whereas this <see cref="BackgroundService"/> is a singleton — the same shape
/// <c>AlertEvaluationWorker</c> uses for the same reason.
/// </summary>
public sealed class ApiKeyTouchWorker(
    IServiceScopeFactory scopeFactory,
    ApiKeyTouchTracker tracker,
    IOptions<TenantResolutionOptions> options,
    ILogger<ApiKeyTouchWorker> logger) : BackgroundService
{
    private readonly TenantResolutionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.LastUsedFlushIntervalSeconds);
        logger.LogInformation("API key touch worker started (interval: {Interval}s).", _options.LastUsedFlushIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var keyHashes = tracker.Drain();
                if (keyHashes.Count > 0)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var store = scope.ServiceProvider.GetRequiredService<IApiKeyTouchStore>();
                    await store.TouchAsync(keyHashes, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error flushing api_keys.last_used_at — will retry next interval");
                // Drained keys are not re-queued on failure: last_used_at is already best-effort,
                // and any key still in active use will simply be marked touched again by the next
                // request and picked up on the next cycle. Re-queueing would risk unbounded growth
                // if the store stays unreachable.
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

        logger.LogInformation("API key touch worker stopped.");
    }
}
