using Microsoft.Extensions.Configuration;
using Npgsql;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.PostgreSQL.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// PostgreSQL (plain) provider registration extensions. The host selects this provider via
/// <c>Database:Provider = "PostgreSQL"</c>. A singleton <see cref="NpgsqlDataSource"/> is
/// built from the relevant connection string and owns the connection pool.
/// </summary>
public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion server (uses <c>ConnectionStrings:Write</c>).</summary>
    public static IServiceCollection AddPostgreSqlWriteServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(configuration.GetConnectionString("Write")!));
        services.AddSingleton<ITelemetryBulkWriter, PostgreSqlBulkWriter>();
        services.AddScoped<ITenantResolver, TenantResolver>();
        services.AddScoped<ITelemetryWriteStore, PostgreSqlWriteStore>();
        return services;
    }

    /// <summary>Read-side services for the Blazor client and the API (uses <c>ConnectionStrings:Read</c>).</summary>
    public static IServiceCollection AddPostgreSqlReadServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(configuration.GetConnectionString("Read")!));
        services.AddScoped<ITraceReadRepository, PostgreSqlTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, PostgreSqlMetricReadRepository>();
        services.AddScoped<ILogReadRepository, PostgreSqlLogReadRepository>();
        services.AddScoped<IAlertRuleRepository, PostgreSqlAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, PostgreSqlTenantCatalogRepository>();
        return services;
    }
}
