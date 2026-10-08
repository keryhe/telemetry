using Microsoft.Extensions.Configuration;
using Npgsql;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.PostgreSQL.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// PostgreSQL provider registration extensions. The host selects this provider for telemetry data via
/// <c>Database:Provider = "PostgreSQL"</c> and/or for the control plane via
/// <c>ControlPlane:Provider = "PostgreSQL"</c>. Each side owns its pool: the telemetry side registers an
/// unkeyed singleton <see cref="NpgsqlDataSource"/>, the control plane a keyed one
/// (<see cref="PostgreSqlControlPlane.ServiceKey"/>), so both can be PostgreSQL in one container.
/// </summary>
public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion server (uses <c>ConnectionStrings:Collector</c>).</summary>
    public static IServiceCollection AddPostgreSqlCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(configuration.GetConnectionString("Collector")!));
        services.AddSingleton<ITelemetryBulkWriter, PostgreSqlBulkWriter>();
        // metric_last_seen maintenance (list-pages-server-side plan, Phase 5). MetricTouchWorker
        // is provider-agnostic and registered once, in AddKeryheTelemetryCollector.
        services.AddScoped<IMetricTouchStore, PostgreSqlMetricTouchStore>();
        // Summary rollups (plans/summary-rollups.md); RollupWorker is registered once, in AddKeryheTelemetryCollector.
        services.AddScoped<IRollupStore, PostgreSqlRollupStore>();
        return services;
    }

    /// <summary>Read-side services for the Blazor client and the API (uses <c>ConnectionStrings:Api</c>).</summary>
    public static IServiceCollection AddPostgreSqlApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(configuration.GetConnectionString("Api")!));
        services.AddScoped<ITraceReadRepository, PostgreSqlTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, PostgreSqlMetricReadRepository>();
        services.AddScoped<ILogReadRepository, PostgreSqlLogReadRepository>();
        services.AddScoped<IRollupReadRepository, PostgreSqlRollupReadRepository>();
        services.AddScoped<IResourceReadRepository, PostgreSqlResourceReadRepository>();
        services.AddScoped<IRetentionSweeper, PostgreSqlRetentionSweeper>();
        services.AddSingleton(ProviderCapabilities.FromConfiguration(ProviderCapabilities.Default(), configuration));
        return services;
    }

    /// <summary>
    /// Control-plane services for the gRPC ingestion server (uses <c>ConnectionStrings:ControlPlane</c>): the API-key
    /// lookup and <c>last_used_at</c> update. <c>CachingTenantResolver</c> (the <c>ITenantResolver</c> every gRPC
    /// service actually resolves) and <c>ApiKeyTouchWorker</c> are provider-agnostic and registered once, in
    /// <c>AddKeryheTelemetryCollector</c>.
    /// </summary>
    public static IServiceCollection AddPostgreSqlControlPlaneCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        AddControlPlaneDataSource(services, configuration);
        services.AddScoped<IApiKeyLookup, TenantResolver>();
        services.AddScoped<IApiKeyTouchStore, PostgreSqlApiKeyTouchStore>();
        return services;
    }

    /// <summary>Control-plane services for the API (uses <c>ConnectionStrings:ControlPlane</c>): alert rules, the tenant catalog and retention settings.</summary>
    public static IServiceCollection AddPostgreSqlControlPlaneApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        AddControlPlaneDataSource(services, configuration);
        services.AddScoped<IAlertRuleRepository, PostgreSqlAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, PostgreSqlTenantCatalogRepository>();
        services.AddScoped<IRetentionSettingsRepository, PostgreSqlRetentionSettingsRepository>();
        return services;
    }

    private static void AddControlPlaneDataSource(IServiceCollection services, IConfiguration configuration)
    {
        // Read now, so a missing ConnectionStrings:ControlPlane fails at startup with the key named.
        var connectionString = ControlPlaneConnection.FromConfiguration(configuration).ConnectionString;
        services.AddKeyedSingleton(PostgreSqlControlPlane.ServiceKey, (_, _) => NpgsqlDataSource.Create(connectionString));
    }
}

/// <summary>Names the control-plane <see cref="NpgsqlDataSource"/> in the container, apart from the telemetry one.</summary>
public static class PostgreSqlControlPlane
{
    public const string ServiceKey = "ControlPlane";
}
