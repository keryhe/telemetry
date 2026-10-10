using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.SqlServer.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SqlServer provider registration extensions. The host selects this provider via
/// <c>Database:Provider = "SqlServer"</c>. Telemetry connection strings come from
/// <c>ConnectionStrings:Collector</c> (server) and <c>ConnectionStrings:Api</c> (client/api). The same provider can
/// also host the control plane (<c>ControlPlane:Provider</c>, <c>ConnectionStrings:ControlPlane</c>).
/// </summary>
public static class SqlServerServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion server.</summary>
    public static IServiceCollection AddSqlServerCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Lets the ingestion worker split a batch around records this database refuses instead of retrying and dropping it.
        services.AddSingleton<Keryhe.Telemetry.Core.Data.IFlushErrorClassifier, Keryhe.Telemetry.SqlServer.Services.SqlServerFlushErrorClassifier>();
        services.AddSingleton<ITelemetryBulkWriter, SqlServerBulkWriter>();
        // metric_last_seen maintenance (list-pages-server-side plan, Phase 5). MetricTouchWorker
        // is provider-agnostic and registered once, in AddKeryheTelemetryCollector.
        services.AddScoped<IMetricTouchStore, SqlServerMetricTouchStore>();
        // Summary rollups (plans/summary-rollups.md); RollupWorker is registered once, in AddKeryheTelemetryCollector.
        services.AddScoped<IRollupStore, SqlServerRollupStore>();
        return services;
    }

    /// <summary>Read-side services for the Blazor client and the API.</summary>
    public static IServiceCollection AddSqlServerApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITraceReadRepository, SqlServerTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, SqlServerMetricReadRepository>();
        services.AddScoped<ILogReadRepository, SqlServerLogReadRepository>();
        services.AddScoped<IRollupReadRepository, SqlServerRollupReadRepository>();
        services.AddScoped<IResourceReadRepository, SqlServerResourceReadRepository>();
        services.AddScoped<IRetentionSweeper, SqlServerRetentionSweeper>();
        services.AddSingleton(ProviderCapabilities.FromConfiguration(ProviderCapabilities.Constrained(), configuration));
        return services;
    }

    /// <summary>
    /// Control-plane services for the gRPC ingestion server (uses <c>ConnectionStrings:ControlPlane</c>): the API-key
    /// lookup and <c>last_used_at</c> update. <c>CachingTenantResolver</c> (the <c>ITenantResolver</c> every gRPC
    /// service actually resolves) and <c>ApiKeyTouchWorker</c> are provider-agnostic and registered once, in
    /// <c>AddKeryheTelemetryCollector</c>.
    /// </summary>
    public static IServiceCollection AddSqlServerControlPlaneCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Read now, so a missing ConnectionStrings:ControlPlane fails at startup with the key named.
        services.AddSingleton(ControlPlaneConnection.FromConfiguration(configuration));
        services.AddScoped<IApiKeyLookup, TenantResolver>();
        services.AddScoped<IApiKeyTouchStore, SqlServerApiKeyTouchStore>();
        return services;
    }

    /// <summary>Control-plane services for the API (uses <c>ConnectionStrings:ControlPlane</c>): alert rules, the tenant catalog and retention settings.</summary>
    public static IServiceCollection AddSqlServerControlPlaneApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(ControlPlaneConnection.FromConfiguration(configuration));
        services.AddScoped<IAlertRuleRepository, SqlServerAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, SqlServerTenantCatalogRepository>();
        services.AddScoped<IRetentionSettingsRepository, SqlServerRetentionSettingsRepository>();
        return services;
    }
}
