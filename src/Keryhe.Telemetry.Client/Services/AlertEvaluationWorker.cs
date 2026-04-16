using Keryhe.Telemetry.Alerting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Client.Services;

public class AlertEvaluationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AlertEvaluationWorker> _logger;
    private readonly int _intervalSeconds;

    public AlertEvaluationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<AlertEvaluationWorker> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _intervalSeconds = configuration.GetValue<int>("AlertEvaluation:IntervalSeconds", 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Alert evaluation worker started (interval: {Interval}s).", _intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var alertService = scope.ServiceProvider.GetRequiredService<IAlertService>();
                await alertService.EvaluateAllAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during alert evaluation cycle.");
            }

            await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Alert evaluation worker stopped.");
    }
}
