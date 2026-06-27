using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data;
using Keryhe.Telemetry.SqlServer.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SqlServer provider registration extensions. The host selects this provider via
/// <c>Database:Provider = "SqlServer"</c>. Connection strings come from
/// <c>ConnectionStrings:Write</c> (server) and <c>ConnectionStrings:Read</c> (client/api).
/// </summary>
public static class SqlServerServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion server.</summary>
    public static IServiceCollection AddSqlServerWriteServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITelemetryBulkWriter, SqlServerBulkWriter>();
        services.AddScoped<ITenantResolver, TenantResolver>();
        services.AddScoped<ITelemetryWriteStore, SqlServerWriteStore>();
        return services;
    }

    /// <summary>Read-side services for the Blazor client and the API.</summary>
    public static IServiceCollection AddSqlServerReadServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITraceReadRepository, SqlServerTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, SqlServerMetricReadRepository>();
        services.AddScoped<ILogReadRepository, SqlServerLogReadRepository>();
        services.AddScoped<IAlertRuleRepository, SqlServerAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, SqlServerTenantCatalogRepository>();
        return services;
    }
}
