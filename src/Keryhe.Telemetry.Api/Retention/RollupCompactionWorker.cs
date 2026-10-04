using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Api.Retention;

/// <summary>
/// Drives the summary rollups' hour tier (plans/summary-rollups.md, Phase 4): every
/// <see cref="RollupOptions.CompactionIntervalSeconds"/> it asks the provider's <see cref="IRollupCompactor"/> to fold
/// closed hours of the minute rollup into the hour tier. Registered by <c>AddRetention</c> on every provider, but only a
/// provider with an hour tier (MySQL) registers a compactor; without one the worker idles. Structurally mirrors
/// <see cref="RetentionWorker"/>.
/// </summary>
public sealed class RollupCompactionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<RollupOptions> options,
    TimeProvider time,
    ILogger<RollupCompactionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var probe = scopeFactory.CreateScope())
        {
            if (probe.ServiceProvider.GetService<IRollupCompactor>() is null)
            {
                logger.LogDebug("No rollup compactor is registered for this provider; the hour tier is not used.");
                return;
            }
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.CompactionIntervalSeconds));
        logger.LogInformation("Rollup compaction worker started (interval: {Interval}s).", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var compactor = scope.ServiceProvider.GetRequiredService<IRollupCompactor>();
                await compactor.CompactAsync(time.GetUtcNow().UtcDateTime, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error compacting the rollup hour tier -- will retry next interval");
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
    }
}
