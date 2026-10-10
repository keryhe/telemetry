using Keryhe.Telemetry.TestDataGenerator.Config;
using Keryhe.Telemetry.TestDataGenerator.Sinks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.TestDataGenerator;

/// <summary>
/// Backfills the configured window of history through hand-built OTLP, then continues in real time through
/// the OpenTelemetry SDK. Both read from the same per-tenant simulation, so the join is seamless.
/// </summary>
public sealed class GeneratorWorker : BackgroundService
{
    private readonly GeneratorOptions _options;
    private readonly ILogger<GeneratorWorker> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public GeneratorWorker(IOptions<GeneratorOptions> options, ILogger<GeneratorWorker> logger, IHostApplicationLifetime lifetime)
    {
        _options = options.Value;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The generator stopped: {Message}", ex.Message);
            Environment.ExitCode = 1;
            _lifetime.StopApplication();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _options.Validate();
        var endpoint = new Uri(_options.OtlpEndpoint);
        var chunkSeconds = Math.Max(1, _options.Backfill.ChunkSeconds);
        var liveStart = FloorTo(DateTimeOffset.UtcNow.AddSeconds(-_options.LiveLagSeconds), chunkSeconds);
        var simulators = _options.Tenants.Select(t => new TenantSimulator(t, _options)).ToList();

        _logger.LogInformation(
            "Generating for {Tenants} (peak {Peak} req/s x scale), seed {Seed}, endpoint {Endpoint}",
            string.Join(", ", _options.Tenants.Select(t => $"{t.Name} x{t.Scale}")), _options.PeakRequestsPerSecond, _options.Seed, endpoint);

        if (_options.Backfill.Enabled)
        {
            var from = liveStart - _options.Backfill.Window;
            _logger.LogInformation("Backfilling {Window} of history: {From:u} to {To:u}", _options.Backfill.Window, from, liveStart);
            var started = DateTimeOffset.UtcNow;
            await Task.WhenAll(simulators.Select((sim, i) => BackfillAsync(sim, _options.Tenants[i], endpoint, from, liveStart, chunkSeconds, ct)));
            _logger.LogInformation("Backfill complete in {Elapsed:mm\\:ss}", DateTimeOffset.UtcNow - started);
        }

        if (!_options.Live)
        {
            _logger.LogInformation("Live emission is disabled; done.");
            _lifetime.StopApplication();
            return;
        }

        _logger.LogInformation("Going live from {Start:u} (trailing the clock by {Lag}s)", liveStart, _options.LiveLagSeconds);
        await Task.WhenAll(simulators.Select((sim, i) => LiveAsync(sim, _options.Tenants[i], endpoint, liveStart, ct)));
    }

    private async Task BackfillAsync(
        TenantSimulator sim, TenantOptions tenant, Uri endpoint, DateTimeOffset from, DateTimeOffset to, int chunkSeconds, CancellationToken ct)
    {
        await using var sink = new OtlpSink(tenant.Name, tenant.ApiKey, endpoint, _options.Backfill.MaxSpansPerExport, _options.Seed, _logger);
        var total = (to - from).TotalSeconds;
        var nextReport = 0.1;
        var step = TimeSpan.FromSeconds(chunkSeconds);
        for (var t = from; t < to; t += step)
        {
            var end = t + step < to ? t + step : to;
            await sink.WriteAsync(sim.Simulate(t, end, includeSamples: true), ct);

            var done = (end - from).TotalSeconds / total;
            if (done >= nextReport)
            {
                _logger.LogInformation("{Tenant}: backfill {Percent:P0} ({Spans:N0} spans, {Logs:N0} logs sent)", tenant.Name, done, sink.SpansSent, sink.LogsSent);
                nextReport += 0.1;
            }
        }
        _logger.LogInformation("{Tenant}: backfill finished with {Spans:N0} spans and {Logs:N0} logs", tenant.Name, sink.SpansSent, sink.LogsSent);
    }

    private async Task LiveAsync(TenantSimulator sim, TenantOptions tenant, Uri endpoint, DateTimeOffset start, CancellationToken ct)
    {
        await using var sink = new SdkSink(endpoint, tenant.ApiKey, _options.LiveMetricIntervalSeconds, _options.Protocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase));
        var cursor = start;
        var lastSample = start;
        var sampleEvery = TimeSpan.FromSeconds(_options.LiveMetricIntervalSeconds);
        var step = TimeSpan.FromSeconds(1);
        var lag = TimeSpan.FromSeconds(_options.LiveLagSeconds);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var target = FloorTo(DateTimeOffset.UtcNow - lag, 1);
                while (cursor < target && !ct.IsCancellationRequested)
                {
                    var next = cursor + step;
                    var sample = next - lastSample >= sampleEvery;
                    await sink.WriteAsync(sim.Simulate(cursor, next, sample), ct);
                    if (sample) lastSample = next;
                    cursor = next;
                }
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        _logger.LogInformation("{Tenant}: stopping live emission ({Pods} pods)", tenant.Name, sink.PodCount);
    }

    private static DateTimeOffset FloorTo(DateTimeOffset t, int seconds)
    {
        var unix = t.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(unix - unix % seconds);
    }
}
