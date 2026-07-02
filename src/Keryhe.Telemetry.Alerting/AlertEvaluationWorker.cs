using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Alerting;

/// <summary>
/// Periodic background worker that drives alert evaluation. On each cycle it creates a fresh
/// DI scope, resolves the scoped <see cref="IAlertService"/>, and runs
/// <see cref="IAlertService.EvaluateAllAsync"/>. A scope is required because the service (and
/// its evaluators / read repositories) are registered scoped, whereas a
/// <see cref="BackgroundService"/> is a singleton.
/// </summary>
public sealed class AlertEvaluationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AlertingOptions> options,
    ILogger<AlertEvaluationWorker> logger) : BackgroundService
{
    private readonly AlertingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Alert evaluation worker disabled via configuration.");
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.IntervalSeconds);
        logger.LogInformation("Alert evaluation worker started (interval: {Interval}s).", _options.IntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var alertService = scope.ServiceProvider.GetRequiredService<IAlertService>();
                await alertService.EvaluateAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error during alert evaluation cycle.");
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

        logger.LogInformation("Alert evaluation worker stopped.");
    }
}
