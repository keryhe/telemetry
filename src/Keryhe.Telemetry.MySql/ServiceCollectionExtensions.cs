using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.MySql.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// MySQL provider registration extensions. The host selects this provider via
/// <c>Database:Provider = "MySql"</c>. Telemetry connection strings come from
/// <c>ConnectionStrings:Collector</c> (server) and <c>ConnectionStrings:Api</c> (client/api). The same provider can
/// also host the control plane (<c>ControlPlane:Provider</c>, <c>ConnectionStrings:ControlPlane</c>).
/// Targets MySQL 8.0+ (native JSON, window functions, CHECK constraints).
/// </summary>
public static class MySqlServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion server.</summary>
    public static IServiceCollection AddMySqlCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Lets the ingestion worker split a batch around records this database refuses instead of retrying and dropping it.
        services.AddSingleton<Keryhe.Telemetry.Core.Data.IFlushErrorClassifier, Keryhe.Telemetry.MySql.Services.MySqlFlushErrorClassifier>();
        services.AddSingleton<ITelemetryBulkWriter, MySqlBulkWriter>();
        // metric_last_seen maintenance (list-pages-server-side plan, Phase 5). MetricTouchWorker
        // is provider-agnostic and registered once, in AddKeryheTelemetryCollector.
        services.AddScoped<IMetricTouchStore, MySqlMetricTouchStore>();
        // Summary rollups (plans/summary-rollups.md); RollupWorker is registered once, in AddKeryheTelemetryCollector.
        services.AddScoped<IRollupStore, MySqlRollupStore>();
        return services;
    }

    /// <summary>Read-side services for the API.</summary>
    public static IServiceCollection AddMySqlApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITraceReadRepository, MySqlTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, MySqlMetricReadRepository>();
        services.AddScoped<ILogReadRepository, MySqlLogReadRepository>();
        services.AddScoped<IRollupReadRepository, MySqlRollupReadRepository>();
        // The hour tier exists on MySQL only (the measurement gate named it); driven by the API host's RollupCompactionWorker.
        services.AddScoped<IRollupCompactor, MySqlRollupCompactor>();
        services.AddScoped<IResourceReadRepository, MySqlResourceReadRepository>();
        services.AddScoped<IRetentionSweeper, MySqlRetentionSweeper>();
        services.AddSingleton(ProviderCapabilities.FromConfiguration(ProviderCapabilities.Constrained(), configuration));
        return services;
    }

    /// <summary>
    /// Control-plane services for the gRPC ingestion server (uses <c>ConnectionStrings:ControlPlane</c>): the API-key
    /// lookup and <c>last_used_at</c> update. <c>CachingTenantResolver</c> (the <c>ITenantResolver</c> every gRPC
    /// service actually resolves) and <c>ApiKeyTouchWorker</c> are provider-agnostic and registered once, in
    /// <c>AddKeryheTelemetryCollector</c>.
    /// </summary>
    public static IServiceCollection AddMySqlControlPlaneCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Read now, so a missing ConnectionStrings:ControlPlane fails at startup with the key named.
        services.AddSingleton(ControlPlaneConnection.FromConfiguration(configuration));
        services.AddScoped<IApiKeyLookup, MySqlTenantResolver>();
        services.AddScoped<IApiKeyTouchStore, MySqlApiKeyTouchStore>();
        return services;
    }

    /// <summary>Control-plane services for the API (uses <c>ConnectionStrings:ControlPlane</c>): alert rules, the tenant catalog and retention settings.</summary>
    public static IServiceCollection AddMySqlControlPlaneApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(ControlPlaneConnection.FromConfiguration(configuration));
        services.AddScoped<IAlertRuleRepository, MySqlAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, MySqlTenantCatalogRepository>();
        services.AddScoped<IRetentionSettingsRepository, MySqlRetentionSettingsRepository>();
        return services;
    }
}
