using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.ClickHouse.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// ClickHouse provider registration extensions. The host selects this provider via
/// <c>Database:Provider = "ClickHouse"</c>. Connection strings come from
/// <c>ConnectionStrings:Write</c> (ingestion collector) and <c>ConnectionStrings:Read</c>
/// (API). Unlike the Postgres provider there is no pooled data-source singleton — like the
/// SqlServer provider, connections are created per operation from the connection string
/// (ClickHouse.Client pools HTTP connections internally).
/// </summary>
public static class ClickHouseServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion collector.</summary>
    public static IServiceCollection AddClickHouseWriteServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITelemetryBulkWriter, ClickHouseBulkWriter>();
        services.AddScoped<ITenantResolver, TenantResolver>();
        services.AddScoped<ITelemetryWriteStore, ClickHouseWriteStore>();
        return services;
    }

    /// <summary>Read-side services for the API.</summary>
    public static IServiceCollection AddClickHouseReadServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITraceReadRepository, ClickHouseTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, ClickHouseMetricReadRepository>();
        services.AddScoped<ILogReadRepository, ClickHouseLogReadRepository>();
        services.AddScoped<IAlertRuleRepository, ClickHouseAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, ClickHouseTenantCatalogRepository>();
        return services;
    }
}
