using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.ClickHouse.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// ClickHouse provider registration extensions. The host selects this provider via
/// <c>Database:Provider = "ClickHouse"</c>. Connection strings come from
/// <c>ConnectionStrings:Collector</c> (ingestion collector) and <c>ConnectionStrings:Api</c>
/// (API). Unlike the Postgres provider there is no pooled data-source singleton — like the
/// SqlServer provider, connections are created per operation from the connection string
/// (ClickHouse.Client pools HTTP connections internally).
/// </summary>
public static class ClickHouseServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion collector.</summary>
    public static IServiceCollection AddClickHouseCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITelemetryBulkWriter, ClickHouseBulkWriter>();
        // The raw api_keys lookup and last_used_at bulk update. CachingTenantResolver (the
        // ITenantResolver every gRPC service actually resolves) and ApiKeyTouchWorker are
        // provider-agnostic and registered once, in AddKeryheTelemetryCollector.
        // ClickHouseApiKeyTouchStore is a deliberate no-op — see that type.
        services.AddScoped<IApiKeyLookup, TenantResolver>();
        services.AddScoped<IApiKeyTouchStore, ClickHouseApiKeyTouchStore>();
        return services;
    }

    /// <summary>Read-side services for the API.</summary>
    public static IServiceCollection AddClickHouseApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITraceReadRepository, ClickHouseTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, ClickHouseMetricReadRepository>();
        services.AddScoped<ILogReadRepository, ClickHouseLogReadRepository>();
        services.AddScoped<IResourceReadRepository, ClickHouseResourceReadRepository>();
        services.AddScoped<IAlertRuleRepository, ClickHouseAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, ClickHouseTenantCatalogRepository>();
        services.AddScoped<IRetentionSettingsRepository, ClickHouseRetentionSettingsRepository>();
        return services;
    }
}
