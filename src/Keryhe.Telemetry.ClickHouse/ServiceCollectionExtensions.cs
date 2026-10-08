using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.ClickHouse.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// ClickHouse provider registration extensions. The host selects this provider via
/// <c>Database:Provider = "ClickHouse"</c>. Connection strings come from
/// <c>ConnectionStrings:Collector</c> (ingestion collector) and <c>ConnectionStrings:Api</c>
/// (API). It has no control plane: a ClickHouse deployment runs the control plane (tenants, API keys,
/// alert rules, retention settings) on PostgreSQL, SQL Server or MySQL (<c>ControlPlane:Provider</c>). Unlike the Postgres provider there is no pooled data-source singleton — like the
/// SqlServer provider, connections are created per operation from the connection string
/// (ClickHouse.Client pools HTTP connections internally).
/// </summary>
public static class ClickHouseServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion collector.</summary>
    public static IServiceCollection AddClickHouseCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITelemetryBulkWriter, ClickHouseBulkWriter>();
        // metric_last_seen maintenance (list-pages-server-side plan, Phase 5): a deliberate no-op
        // here (materialized views feed the table instead) — see ClickHouseMetricTouchStore's own
        // doc comment. MetricTouchWorker is still registered unconditionally in
        // AddKeryheTelemetryCollector; it will just drain to nothing on this provider.
        services.AddScoped<IMetricTouchStore, ClickHouseMetricTouchStore>();
        // Summary rollups (plans/summary-rollups.md); RollupWorker is registered once, in AddKeryheTelemetryCollector.
        services.AddScoped<IRollupStore, ClickHouseRollupStore>();
        return services;
    }

    /// <summary>Read-side services for the API.</summary>
    public static IServiceCollection AddClickHouseApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITraceReadRepository, ClickHouseTraceReadRepository>();
        services.AddScoped<IMetricReadRepository, ClickHouseMetricReadRepository>();
        services.AddScoped<ILogReadRepository, ClickHouseLogReadRepository>();
        services.AddScoped<IRollupReadRepository, ClickHouseRollupReadRepository>();
        services.AddScoped<IResourceReadRepository, ClickHouseResourceReadRepository>();
        services.AddScoped<IRetentionSweeper, ClickHouseRetentionSweeper>();
        services.AddSingleton(ProviderCapabilities.FromConfiguration(ProviderCapabilities.Default(), configuration));
        return services;
    }
}
