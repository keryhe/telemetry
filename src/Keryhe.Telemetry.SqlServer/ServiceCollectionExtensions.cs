using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.SqlServer.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SqlServer provider registration extensions. The host selects this provider via
/// <c>Database:Provider = "SqlServer"</c>. Connection strings come from
/// <c>ConnectionStrings:Collector</c> (server) and <c>ConnectionStrings:Api</c> (client/api).
/// </summary>
public static class SqlServerServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion server.</summary>
    public static IServiceCollection AddSqlServerCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITelemetryBulkWriter, SqlServerBulkWriter>();
        // The raw api_keys lookup and last_used_at bulk update. CachingTenantResolver (the
        // ITenantResolver every gRPC service actually resolves) and ApiKeyTouchWorker are
        // provider-agnostic and registered once, in AddKeryheTelemetryCollector.
        services.AddScoped<IApiKeyLookup, TenantResolver>();
        services.AddScoped<IApiKeyTouchStore, SqlServerApiKeyTouchStore>();
        return services;
    }

    /// <summary>Read-side services for the Blazor client and the API.</summary>
    public static IServiceCollection AddSqlServerApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITraceReadRepository, SqlServerTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, SqlServerMetricReadRepository>();
        services.AddScoped<ILogReadRepository, SqlServerLogReadRepository>();
        services.AddScoped<IResourceReadRepository, SqlServerResourceReadRepository>();
        services.AddScoped<IAlertRuleRepository, SqlServerAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, SqlServerTenantCatalogRepository>();
        services.AddScoped<IRetentionSettingsRepository, SqlServerRetentionSettingsRepository>();
        return services;
    }
}
