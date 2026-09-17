using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Api.Retention;

/// <summary>
/// Periodic background worker that drives the retention sweeps. On each cycle it creates a
/// fresh DI scope, resolves the scoped <see cref="IRetentionSettingsRepository"/>, reads the
/// current windows via <see cref="IRetentionSettingsRepository.GetSettingsAsync"/>, then runs
/// the three <c>Delete*</c> sweeps against them. A scope is required because the repository (and
/// the provider connection it holds) is registered scoped, whereas a <see cref="BackgroundService"/>
/// is a singleton — structurally a copy of <c>AlertEvaluationWorker</c>.
/// </summary>
public sealed class RetentionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<RetentionOptions> options,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    private readonly RetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Retention worker disabled via configuration.");
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.IntervalSeconds);
        logger.LogInformation("Retention worker started (interval: {Interval}s).", _options.IntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>();
                var settings = await repository.GetSettingsAsync(stoppingToken);

                var tracesRemoved = await repository.DeleteOldTracesAsync(
                    TimeSpan.FromDays(settings.TraceRetentionDays), stoppingToken);
                var metricsRemoved = await repository.DeleteOldMetricDataPointsAsync(
                    TimeSpan.FromDays(settings.MetricRetentionDays), stoppingToken);
                var logsRemoved = await repository.DeleteOldLogRecordsAsync(
                    TimeSpan.FromDays(settings.LogRetentionDays), stoppingToken);

                logger.LogInformation(
                    "Retention sweep complete: {Traces} span rows, {Metrics} data-point rows, {Logs} log rows removed.",
                    tracesRemoved, metricsRemoved, logsRemoved);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error during retention sweep.");
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

        logger.LogInformation("Retention worker stopped.");
    }
}
