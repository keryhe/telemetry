using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Write;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods that register the Keryhe Telemetry collector (gRPC OTLP ingestion and
/// the provider-agnostic ingestion channel + worker) into a host application. Mirrors
/// <c>AddKeryheTelemetryApi</c> on the read side.
/// </summary>
public static class TelemetryCollectorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the write path: gRPC, the ingestion channel (gated on resident record/span
    /// count — see <see cref="TelemetryIngestionOptions"/>), and the background worker that
    /// drains it. This does not register a database provider — the host must also call the
    /// active provider's <c>Add&lt;Provider&gt;CollectorServices(configuration)</c> (e.g.
    /// <c>AddPostgreSqlCollectorServices</c>), which supplies <c>ITelemetryBulkWriter</c>,
    /// <c>IApiKeyLookup</c>, and <c>IApiKeyTouchStore</c> (connection string comes from
    /// <c>ConnectionStrings:Collector</c>). The host still owns CORS, Kestrel configuration, and
    /// calling <c>MapKeryheTelemetryCollector()</c>.
    /// </summary>
    public static IServiceCollection AddKeryheTelemetryCollector(this IServiceCollection services, IConfiguration configuration)
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
        // (via the host's Add<Provider>CollectorServices call). Bound from Telemetry:TenantResolution.
        services.Configure<TenantResolutionOptions>(configuration.GetSection(TenantResolutionOptions.SectionName));
        services.AddMemoryCache();
        services.AddSingleton<ApiKeyTouchTracker>();
        services.AddScoped<ITenantResolver, CachingTenantResolver>();
        services.AddHostedService<ApiKeyTouchWorker>();

        // Write path: the generic worker drains the ingestion channel and delegates each
        // batch flush to the active provider's ITelemetryBulkWriter. The host is responsible
        // for registering that provider (ITelemetryBulkWriter, IApiKeyLookup,
        // IApiKeyTouchStore) via the matching Add<Provider>CollectorServices call.
        services.AddHostedService<TelemetryIngestionWorker>();

        services
            .AddScoped<ILogWriteRepository, LogWriteRepository>()
            .AddScoped<IMetricWriteRepository, MetricWriteRepository>()
            .AddScoped<ITraceWriteRepository, TraceWriteRepository>();

        return services;
    }
}
