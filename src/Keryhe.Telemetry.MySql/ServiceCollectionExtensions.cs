using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.MySql.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// MySQL provider registration extensions. The host selects this provider via
/// <c>Database:Provider = "MySql"</c>. Connection strings come from
/// <c>ConnectionStrings:Collector</c> (server) and <c>ConnectionStrings:Api</c> (client/api).
/// Targets MySQL 8.0+ (native JSON, window functions, CHECK constraints).
/// </summary>
public static class MySqlServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion server.</summary>
    public static IServiceCollection AddMySqlCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITelemetryBulkWriter, MySqlBulkWriter>();
        // The raw api_keys lookup and last_used_at bulk update. CachingTenantResolver (the
        // ITenantResolver every gRPC service actually resolves) and ApiKeyTouchWorker are
        // provider-agnostic and registered once, in AddKeryheTelemetryCollector.
        services.AddScoped<IApiKeyLookup, MySqlTenantResolver>();
        services.AddScoped<IApiKeyTouchStore, MySqlApiKeyTouchStore>();
        // metric_last_seen maintenance (list-pages-server-side plan, Phase 5). MetricTouchWorker
        // is provider-agnostic and registered once, in AddKeryheTelemetryCollector.
        services.AddScoped<IMetricTouchStore, MySqlMetricTouchStore>();
        return services;
    }

    /// <summary>Read-side services for the API.</summary>
    public static IServiceCollection AddMySqlApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITraceReadRepository, MySqlTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, MySqlMetricReadRepository>();
        services.AddScoped<ILogReadRepository, MySqlLogReadRepository>();
        services.AddScoped<IResourceReadRepository, MySqlResourceReadRepository>();
        services.AddScoped<IAlertRuleRepository, MySqlAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, MySqlTenantCatalogRepository>();
        services.AddScoped<IRetentionSettingsRepository, MySqlRetentionSettingsRepository>();
        services.AddScoped<IRollupRepository, MySqlLogRollupRepository>();
        services.AddSingleton(ProviderCapabilities.FromConfiguration(ProviderTier.Standard, configuration));
        return services;
    }
}
