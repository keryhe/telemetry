using Microsoft.Extensions.Configuration;
using Npgsql;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.PostgreSQL.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// PostgreSQL (plain) provider registration extensions. The host selects this provider via
/// <c>Database:Provider = "PostgreSQL"</c>. A singleton <see cref="NpgsqlDataSource"/> is
/// built from the relevant connection string and owns the connection pool.
/// </summary>
public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion server (uses <c>ConnectionStrings:Collector</c>).</summary>
    public static IServiceCollection AddPostgreSqlCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(configuration.GetConnectionString("Collector")!));
        services.AddSingleton<ITelemetryBulkWriter, PostgreSqlBulkWriter>();
        // The raw api_keys lookup and last_used_at bulk update. CachingTenantResolver (the
        // ITenantResolver every gRPC service actually resolves) and ApiKeyTouchWorker are
        // provider-agnostic and registered once, in AddKeryheTelemetryCollector.
        services.AddScoped<IApiKeyLookup, TenantResolver>();
        services.AddScoped<IApiKeyTouchStore, PostgreSqlApiKeyTouchStore>();
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
        services.AddScoped<IAlertRuleRepository, PostgreSqlAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, PostgreSqlTenantCatalogRepository>();
        services.AddScoped<IRetentionSettingsRepository, PostgreSqlRetentionSettingsRepository>();
        services.AddSingleton(ProviderCapabilities.FromConfiguration(ProviderCapabilities.Default(), configuration) with
        {
            // The read repositories keep DapperReadRepository's default asOf expression, which backs off this far.
            AsOfBackoffSeconds = DapperReadRepository.PostgresAsOfBackoffSeconds
        });
        return services;
    }
}
