using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Keryhe.Telemetry.TestDataGenerator.Generators;

namespace Keryhe.Telemetry.TestDataGenerator;

/// <summary>
/// Background worker service that periodically generates and sends telemetry data.
/// </summary>
public class TelemetryGeneratorWorker : BackgroundService
{
    private readonly ILogger<TelemetryGeneratorWorker> _logger;
    private readonly GeneratorConfig _config;
    private readonly ActivitySource? _activitySource;
    private readonly Meter? _meter;
    private readonly ILogger<LogGenerator> _loggerForLogGen;
    private TraceGenerator? _traceGenerator;
    private MetricGenerator? _metricGenerator;
    private LogGenerator? _logGenerator;

    public TelemetryGeneratorWorker(
        ILogger<TelemetryGeneratorWorker> logger,
        IOptions<GeneratorConfig> config,
        ActivitySource? activitySource,
        Meter? meter,
        ILogger<LogGenerator> loggerForLogGen)
    {
        _logger = logger;
        _config = config.Value;
        _activitySource = activitySource;
        _meter = meter;
        _loggerForLogGen = loggerForLogGen;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting telemetry test data generator");
        _logger.LogInformation("Configuration: Mode={Mode}, Interval={Interval}s, Spans={Spans}, Metrics={Metrics}, Logs={Logs}",
            _config.GeneratorMode,
            _config.EmissionIntervalSeconds,
            _config.SpansPerBatch,
            _config.MetricsPerBatch,
            _config.LogsPerBatch);

        if (_config.GeneratorMode == "LoadTest")
        {
            _logger.LogWarning("Running in LOAD TEST mode with higher data volumes");
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initialize generators
        if (_activitySource != null)
        {
            _traceGenerator = new TraceGenerator(_activitySource);
        }

        if (_meter != null)
        {
            _metricGenerator = new MetricGenerator(_meter);
        }

        _logGenerator = new LogGenerator(_loggerForLogGen, _activitySource);

        var interval = TimeSpan.FromSeconds(_config.EmissionIntervalSeconds);
        var emissionCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                emissionCount++;
                var startTime = DateTime.UtcNow;

                // Generate telemetry
                if (_traceGenerator != null)
                {
                    _traceGenerator.GenerateBatch(_config.SpansPerBatch);
                }

                if (_metricGenerator != null)
                {
                    _metricGenerator.GenerateBatch(_config.MetricsPerBatch);
                }

                if (_logGenerator != null)
                {
                    _logGenerator.GenerateBatch(_config.LogsPerBatch);
                }

                var elapsed = DateTime.UtcNow - startTime;
                _logger.LogInformation(
                    "Emission #{Count}: Generated {Spans} spans, {Metrics} metrics, {Logs} logs (elapsed: {ElapsedMs}ms)",
                    emissionCount,
                    _config.SpansPerBatch,
                    _config.MetricsPerBatch,
                    _config.LogsPerBatch,
                    elapsed.TotalMilliseconds
                );

                // Wait for next interval
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Telemetry generator cancellation requested");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating telemetry");
                // Continue running, wait before retry
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping telemetry test data generator");
        return base.StopAsync(cancellationToken);
    }
}
