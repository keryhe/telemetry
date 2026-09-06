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
    private readonly IReadOnlyList<ServiceSource> _serviceSources;
    private readonly ActivitySource? _activitySource;
    private readonly Meter? _meter;
    private readonly ILogger<LogGenerator> _loggerForLogGen;
    private TraceGenerator? _traceGenerator;
    private MetricGenerator? _metricGenerator;
    private LogGenerator? _logGenerator;

    public TelemetryGeneratorWorker(
        ILogger<TelemetryGeneratorWorker> logger,
        IOptions<GeneratorConfig> config,
        IReadOnlyList<ServiceSource> serviceSources,
        ActivitySource? activitySource,
        Meter? meter,
        ILogger<LogGenerator> loggerForLogGen)
    {
        _logger = logger;
        _config = config.Value;
        _serviceSources = serviceSources;
        _activitySource = activitySource;
        _meter = meter;
        _loggerForLogGen = loggerForLogGen;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting telemetry test data generator");
        var isLoadTest = _config.GeneratorMode == "LoadTest";
        _logger.LogInformation("Configuration: Mode={Mode}, Interval={Interval}s, Spans={Spans}, Metrics={Metrics}, Logs={Logs}, Services={ServiceCount}",
            _config.GeneratorMode,
            isLoadTest ? _config.LoadTestEmissionIntervalSeconds : _config.EmissionIntervalSeconds,
            isLoadTest ? _config.LoadTestSpansPerBatch   : _config.SpansPerBatch,
            isLoadTest ? _config.LoadTestMetricsPerBatch : _config.MetricsPerBatch,
            isLoadTest ? _config.LoadTestLogsPerBatch    : _config.LogsPerBatch,
            _serviceSources.Count);

        foreach (var ss in _serviceSources)
        {
            _logger.LogInformation("  Simulated service: {Name} v{Version} (root={CanBeRoot})",
                ss.Definition.Name, ss.Definition.Version, ss.Definition.CanBeRootService);
        }

        if (_config.GeneratorMode == "LoadTest")
        {
            _logger.LogWarning("Running in LOAD TEST mode with higher data volumes");
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _traceGenerator = new TraceGenerator(_serviceSources);

        if (_meter != null)
        {
            _metricGenerator = new MetricGenerator(_meter);
        }

        _logGenerator = new LogGenerator(_loggerForLogGen, _activitySource);

        var isLoadTest = _config.GeneratorMode == "LoadTest";
        var interval       = TimeSpan.FromSeconds(isLoadTest ? _config.LoadTestEmissionIntervalSeconds : _config.EmissionIntervalSeconds);
        var spansPerBatch  = isLoadTest ? _config.LoadTestSpansPerBatch   : _config.SpansPerBatch;
        var metricsPerBatch = isLoadTest ? _config.LoadTestMetricsPerBatch : _config.MetricsPerBatch;
        var logsPerBatch   = isLoadTest ? _config.LoadTestLogsPerBatch    : _config.LogsPerBatch;
        var emissionCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                emissionCount++;
                var startTime = DateTime.UtcNow;

                // Generate telemetry
                _traceGenerator.GenerateBatch(spansPerBatch);

                if (_metricGenerator != null)
                {
                    _metricGenerator.GenerateBatch(metricsPerBatch);
                }

                if (_logGenerator != null)
                {
                    _logGenerator.GenerateBatch(logsPerBatch);
                }

                var elapsed = DateTime.UtcNow - startTime;
                _logger.LogInformation(
                    "Emission #{Count}: Generated {Spans} spans, {Metrics} metrics, {Logs} logs (elapsed: {ElapsedMs}ms)",
                    emissionCount,
                    spansPerBatch,
                    metricsPerBatch,
                    logsPerBatch,
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
