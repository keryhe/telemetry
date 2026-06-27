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
    /// <summary>Write-side services for the gRPC ingestion server (uses <c>ConnectionStrings:Write</c>).</summary>
    public static IServiceCollection AddTimescaleWriteServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(configuration.GetConnectionString("Write")!));
        services.AddSingleton<ITelemetryBulkWriter, TimescaleBulkWriter>();
        services.AddScoped<ITenantResolver, TenantResolver>();
        services.AddScoped<ITelemetryWriteStore, TimescaleWriteStore>();
        return services;
    }

    /// <summary>Read-side services for the Blazor client and the API (uses <c>ConnectionStrings:Read</c>).</summary>
    public static IServiceCollection AddTimescaleReadServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(configuration.GetConnectionString("Read")!));
        services.AddScoped<ITraceReadRepository, TimescaleTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, TimescaleMetricReadRepository>();
        services.AddScoped<ILogReadRepository, TimescaleLogReadRepository>();
        services.AddScoped<IAlertRuleRepository, TimescaleAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, TimescaleTenantCatalogRepository>();
        return services;
    }
}
