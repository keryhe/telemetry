using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data;
using Keryhe.Telemetry.MySql.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// MySQL provider registration extensions. The host selects this provider via
/// <c>Database:Provider = "MySql"</c>. Connection strings come from
/// <c>ConnectionStrings:Write</c> (server) and <c>ConnectionStrings:Read</c> (client/api).
/// Targets MySQL 8.0+ (native JSON, window functions, CHECK constraints).
/// </summary>
public static class MySqlServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion server.</summary>
    public static IServiceCollection AddMySqlWriteServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITelemetryBulkWriter, MySqlBulkWriter>();
        services.AddScoped<ITenantResolver, MySqlTenantResolver>();
        services.AddScoped<ITelemetryWriteStore, MySqlWriteStore>();
        return services;
    }

    /// <summary>Read-side services for the API.</summary>
    public static IServiceCollection AddMySqlReadServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITraceReadRepository, MySqlTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, MySqlMetricReadRepository>();
        services.AddScoped<ILogReadRepository, MySqlLogReadRepository>();
        services.AddScoped<IAlertRuleRepository, MySqlAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, MySqlTenantCatalogRepository>();
        return services;
    }
}
