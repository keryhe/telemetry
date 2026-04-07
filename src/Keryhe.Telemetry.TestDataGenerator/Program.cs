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

        // Create ActivitySource for traces
        ActivitySource? activitySource = new ActivitySource(
            generatorConfig.ServiceName,
            generatorConfig.ServiceVersion
        );

        // Create Meter for metrics
        Meter? meter = new Meter(
            generatorConfig.ServiceName,
            generatorConfig.ServiceVersion
        );

        services.AddSingleton(activitySource);
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

// Configure OpenTelemetry Tracing
var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(config.ServiceName, serviceVersion: config.ServiceVersion))
    .AddSource(config.ServiceName)
    .AddOtlpExporter(exporterOptions =>
    {
        exporterOptions.Endpoint = otlpEndpoint;
        exporterOptions.Protocol = OtlpExportProtocol.Grpc;
    })
    .Build();

// Configure OpenTelemetry Metrics
var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(config.ServiceName, serviceVersion: config.ServiceVersion))
    .AddMeter(config.ServiceName)
    .AddOtlpExporter(exporterOptions =>
    {
        exporterOptions.Endpoint = otlpEndpoint;
        exporterOptions.Protocol = OtlpExportProtocol.Grpc;
    })
    .Build();

await host.RunAsync();

// Graceful cleanup
tracerProvider?.ForceFlush();
tracerProvider?.Dispose();
meterProvider?.ForceFlush();
meterProvider?.Dispose();
