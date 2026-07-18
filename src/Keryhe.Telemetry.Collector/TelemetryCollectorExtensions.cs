using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods that register the Keryhe Telemetry collector (gRPC OTLP ingestion,
/// the ingestion channel + worker, and the active provider's write services) into a host
/// application. Mirrors <c>AddKeryheTelemetryApi</c> on the read side.
/// </summary>
public static class TelemetryCollectorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the write path: gRPC, the bounded ingestion channel, the background
    /// worker that drains it, and the provider selected by <c>Database:Provider</c>
    /// (connection string comes from <c>ConnectionStrings:Write</c>). The host still owns
    /// CORS, Kestrel configuration, and calling <c>MapKeryheTelemetryCollector()</c>.
    /// </summary>
    public static IServiceCollection AddKeryheTelemetryCollector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddGrpc();
        services.AddLogging();

        // Singletons shared across all gRPC requests and the background worker.
        services.AddSingleton<TelemetryIngestionChannel>();
        services.AddSingleton<ResourceScopeCache>();

        // Write path: the generic worker drains the ingestion channel and delegates each
        // batch flush to the active provider's ITelemetryBulkWriter. The provider — and with
        // it ITelemetryBulkWriter, ITenantResolver, and ITelemetryWriteStore — is selected by
        // the Database:Provider config key.
        switch (configuration["Database:Provider"])
        {
            case "SqlServer":  services.AddSqlServerWriteServices(configuration);  break;
            case "PostgreSQL": services.AddPostgreSqlWriteServices(configuration); break;
            case "Timescale":  services.AddTimescaleWriteServices(configuration);  break;
            case "ClickHouse": services.AddClickHouseWriteServices(configuration); break;
            case "MySql":      services.AddMySqlWriteServices(configuration);      break;
            default: throw new InvalidOperationException(
                "Unknown or missing Database:Provider (expected SqlServer, PostgreSQL, Timescale, ClickHouse, or MySql).");
        }

        services.AddHostedService<TelemetryIngestionWorker>();

        services
            .AddScoped<ILogWriteRepository, LogWriteRepository>()
            .AddScoped<IMetricWriteRepository, MetricWriteRepository>()
            .AddScoped<ITraceWriteRepository, TraceWriteRepository>();

        return services;
    }
}
