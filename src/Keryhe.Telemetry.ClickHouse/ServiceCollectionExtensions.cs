using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
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
        services.AddSingleton<ClickHouseBulkWriter>();
        services.AddSingleton<ITelemetryBulkWriter>(sp => sp.GetRequiredService<ClickHouseBulkWriter>());
        services.AddSingleton<IClickHouseTokenWriter>(sp => sp.GetRequiredService<ClickHouseBulkWriter>());

        // ClickHouse batches for itself (plans/clickhouse-redesign phase 2): swap the shared worker for
        // ClickHouseIngestionWorker. AddKeryheTelemetryCollector must have run first (the order Collector.Server uses)
        // for the swap to find the shared worker; a container that never registered it (the integration fixtures write
        // through ITelemetryBulkWriter and run no hosted services) simply gets ours.
        services.AddOptions<ClickHouseIngestionOptions>()
            .Bind(configuration.GetSection(ClickHouseIngestionOptions.SectionName))
            .Validate(o => { o.Validate(); return true; })
            .ValidateOnStart();
        // A full queue frees room a whole day buffer at a time, so the default bounded wait before refusing an export is
        // the linger plus the usual 2 s (an explicit Telemetry:Ingestion:MaxGateWaitMilliseconds still wins).
        services.AddOptions<TelemetryIngestionOptions>().PostConfigure<IOptions<ClickHouseIngestionOptions>>((o, ch) =>
            o.MaxGateWaitMilliseconds ??= ch.Value.LingerMilliseconds + TelemetryIngestionOptions.DefaultMaxGateWaitMilliseconds);
        services.AddSingleton<IFlushErrorClassifier, ClickHouseFlushErrorClassifier>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IRetentionWindows, RetentionWindowCache>();
        var shared = services.FirstOrDefault(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(TelemetryIngestionWorker));
        if (shared is not null) services.Remove(shared);
        else if (services.Any(d => d.ServiceType == typeof(TelemetryIngestionChannel)))
            throw new InvalidOperationException("The shared TelemetryIngestionWorker registration was not found; call AddKeryheTelemetryCollector before AddClickHouseCollectorServices.");
        services.AddHostedService<ClickHouseIngestionWorker>();
        // The catalog's last-seen maintenance: a deliberate no-op here (the writer maintains
        // metric_catalog itself) — see ClickHouseMetricTouchStore's own doc comment. MetricTouchWorker is still registered unconditionally in
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
