using Microsoft.Extensions.Configuration;
using Npgsql;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Timescale.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// TimescaleDB provider registration extensions. The host selects this provider via
/// <c>Database:Provider = "Timescale"</c>. The read repositories derive from the plain
/// PostgreSQL implementations; only the write path differs (hypertable-aware bulk insert).
/// A singleton <see cref="NpgsqlDataSource"/> is built from the relevant connection string.
/// </summary>
public static class TimescaleServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion server (uses <c>ConnectionStrings:Collector</c>).</summary>
    public static IServiceCollection AddTimescaleCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(configuration.GetConnectionString("Collector")!));
        services.AddSingleton<ITelemetryBulkWriter, TimescaleBulkWriter>();
        // The raw api_keys lookup and last_used_at bulk update. CachingTenantResolver (the
        // ITenantResolver every gRPC service actually resolves) and ApiKeyTouchWorker are
        // provider-agnostic and registered once, in AddKeryheTelemetryCollector.
        services.AddScoped<IApiKeyLookup, TenantResolver>();
        services.AddScoped<IApiKeyTouchStore, TimescaleApiKeyTouchStore>();
        return services;
    }

    /// <summary>Read-side services for the Blazor client and the API (uses <c>ConnectionStrings:Api</c>).</summary>
    public static IServiceCollection AddTimescaleApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(configuration.GetConnectionString("Api")!));
        services.AddScoped<ITraceReadRepository, TimescaleTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, TimescaleMetricReadRepository>();
        services.AddScoped<ILogReadRepository, TimescaleLogReadRepository>();
        services.AddScoped<IAlertRuleRepository, TimescaleAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, TimescaleTenantCatalogRepository>();
        services.AddScoped<IRetentionSettingsRepository, TimescaleRetentionSettingsRepository>();
        return services;
    }
}
