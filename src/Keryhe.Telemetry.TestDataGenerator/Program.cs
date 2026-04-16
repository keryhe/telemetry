using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Keryhe.Telemetry.TestDataGenerator;
using Keryhe.Telemetry.TestDataGenerator.Generators;

var host = new HostBuilder()
    .ConfigureAppConfiguration((context, config) =>
    {
        config
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        // Register configuration
        services.Configure<GeneratorConfig>(context.Configuration.GetSection("GeneratorConfig"));
        var generatorConfig = context.Configuration.GetSection("GeneratorConfig").Get<GeneratorConfig>()
            ?? new GeneratorConfig();

        // Build service sources: one ActivitySource per simulated service
        List<ServiceSource> serviceSources;
        if (generatorConfig.Services.Count > 0)
        {
            serviceSources = generatorConfig.Services
                .Select(def => new ServiceSource
                {
                    ActivitySource = new ActivitySource(def.Name, def.Version),
                    Definition = def
                })
                .ToList();
        }
        else
        {
            // Fallback: single service using top-level ServiceName / ServiceVersion
            serviceSources =
            [
                new ServiceSource
                {
                    ActivitySource = new ActivitySource(generatorConfig.ServiceName, generatorConfig.ServiceVersion),
                    Definition = new ServiceDefinition
                    {
                        Name = generatorConfig.ServiceName,
                        Version = generatorConfig.ServiceVersion,
                        CanBeRootService = true
                    }
                }
            ];
        }

        services.AddSingleton<IReadOnlyList<ServiceSource>>(serviceSources);

        // Keep a single ActivitySource registered for the Meter and LogGenerator
        services.AddSingleton(serviceSources[0].ActivitySource);

        // Create Meter for metrics (single service)
        Meter? meter = new Meter(
            generatorConfig.ServiceName,
            generatorConfig.ServiceVersion
        );
        services.AddSingleton(meter);

        // Register background worker
        services.AddHostedService<TelemetryGeneratorWorker>();

        // Add logging
        services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddConsole();
        });
    })
    .ConfigureLogging((context, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        
        // Configure log levels
        logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
        logging.SetMinimumLevel(
            context.HostingEnvironment.IsDevelopment() ? LogLevel.Debug : LogLevel.Information
        );

        var otlpEndpoint = context.Configuration.GetSection("GeneratorConfig:OtlpEndpoint").Value
            ?? "http://localhost:5117";
        var serviceName = context.Configuration.GetSection("GeneratorConfig:ServiceName").Value
            ?? "telemetry-test-generator";
        var serviceVersion = context.Configuration.GetSection("GeneratorConfig:ServiceVersion").Value
            ?? "1.0.0";

        // Export ILogger logs to OTLP so they are stored by the telemetry server.
        logging.AddOpenTelemetry(otlpLogging =>
        {
            otlpLogging.IncludeFormattedMessage = true;
            otlpLogging
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName, serviceVersion: serviceVersion))
                .AddOtlpExporter(exporterOptions =>
                {
                    exporterOptions.Endpoint = new Uri(otlpEndpoint);
                    exporterOptions.Protocol = OtlpExportProtocol.Grpc;
                });
        });
    })
    .Build();

// Get configuration for OpenTelemetry setup
var config = host.Services.GetRequiredService<IOptions<GeneratorConfig>>()?.Value
    ?? new GeneratorConfig();

var otlpEndpoint = new Uri(config.OtlpEndpoint);

// Create one TracerProvider per simulated service so each service gets its own
// service.name resource attribute in exported spans.
var serviceSources = host.Services.GetRequiredService<IReadOnlyList<ServiceSource>>();
var tracerProviders = serviceSources
    .Select(ss => Sdk.CreateTracerProviderBuilder()
        .SetResourceBuilder(ResourceBuilder.CreateDefault()
            .AddService(ss.Definition.Name, serviceVersion: ss.Definition.Version))
        .AddSource(ss.Definition.Name)
        .AddOtlpExporter(exporterOptions =>
        {
            exporterOptions.Endpoint = otlpEndpoint;
            exporterOptions.Protocol = OtlpExportProtocol.Grpc;
        })
        .Build())
    .ToList();

// Configure OpenTelemetry Metrics (single provider for the primary service)
var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(config.ServiceName, serviceVersion: config.ServiceVersion))
    .AddMeter(config.ServiceName)
    .AddOtlpExporter((exporterOptions, metricReaderOptions) =>
    {
        exporterOptions.Endpoint = otlpEndpoint;
        exporterOptions.Protocol = OtlpExportProtocol.Grpc;
        metricReaderOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 15_000;
    })
    .Build();

await host.RunAsync();

// Dispose all tracer providers
foreach (var tp in tracerProviders)
{
    tp.Dispose();
}
meterProvider.Dispose();
