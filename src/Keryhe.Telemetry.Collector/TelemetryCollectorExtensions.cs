using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Write;
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
    /// Registers the write path: gRPC, the ingestion channel (gated on resident record/span
    /// count — see <see cref="TelemetryIngestionOptions"/>), the background worker that drains
    /// it, and the provider selected by <c>Database:Provider</c> (connection string comes from
    /// <c>ConnectionStrings:Collector</c>). The host still owns CORS, Kestrel configuration, and
    /// calling <c>MapKeryheTelemetryCollector()</c>.
    /// </summary>
    public static IServiceCollection AddKeryheTelemetryCollector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddGrpc();
        services.AddLogging();

        // Ingestion queue/batch limits — see TelemetryIngestionOptions for why records (spans for
        // traces) rather than batches are what these bound. Bound from Telemetry:Ingestion.
        services.Configure<TelemetryIngestionOptions>(configuration.GetSection(TelemetryIngestionOptions.SectionName));

        // Singletons shared across all gRPC requests and the background worker.
        services.AddSingleton<TelemetryIngestionChannel>();
        services.AddSingleton<ResourceScopeCache>();
        services.AddSingleton<IngestionMetrics>();

        // Tenant resolution: CachingTenantResolver / ApiKeyTouchWorker are registered once, here,
        // provider-agnostically — every provider's ITenantResolver used to do its own SELECT *and*
        // UPDATE on every gRPC export against the one api_keys row a whole tenant's agents share.
        // Now every provider registers only the raw IApiKeyLookup / IApiKeyTouchStore this wraps
        // (see the switch below). Bound from Telemetry:TenantResolution.
        services.Configure<TenantResolutionOptions>(configuration.GetSection(TenantResolutionOptions.SectionName));
        services.AddMemoryCache();
        services.AddSingleton<ApiKeyTouchTracker>();
        services.AddScoped<ITenantResolver, CachingTenantResolver>();
        services.AddHostedService<ApiKeyTouchWorker>();

        // Write path: the generic worker drains the ingestion channel and delegates each
        // batch flush to the active provider's ITelemetryBulkWriter. The provider — and with
        // it ITelemetryBulkWriter, IApiKeyLookup, and IApiKeyTouchStore —
        // is selected by the Database:Provider config key.
        switch (configuration["Database:Provider"])
        {
            case "SqlServer":  services.AddSqlServerCollectorServices(configuration);  break;
            case "PostgreSQL": services.AddPostgreSqlCollectorServices(configuration); break;
            case "Timescale":  services.AddTimescaleCollectorServices(configuration);  break;
            case "ClickHouse": services.AddClickHouseCollectorServices(configuration); break;
            case "MySql":      services.AddMySqlCollectorServices(configuration);      break;
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
