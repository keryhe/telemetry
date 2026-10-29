using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Sqlite;
using Keryhe.Telemetry.Sqlite.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SQLite control-plane registration extensions. The host selects them with <c>Telemetry:{Collector|Api}:Provider:ControlPlane = "Sqlite"</c>; the database is
/// the file named by <c>ConnectionStrings:ControlPlane</c> (<c>Data Source=&lt;absolute path&gt;</c>). SQLite is a control-plane provider
/// only, never a <c>Telemetry:{Collector|Api}:Provider:Database</c>, and every process that opens the file must run on the same machine's local file system.
/// </summary>
public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// The API-key side of the control plane, for the collector: the key lookup and the <c>last_used_at</c> update, and the startup step
    /// that creates the database (and its directory) or migrates it forward, so the collector can start before the API host
    /// (<c>Telemetry:ControlPlane:Sqlite:ApplySchema</c>, default true; false only checks). <paramref name="fixedTenantId"/> (a fixed-tenant
    /// collector) is created, named <c>Default</c>, when it has no row. <c>CachingTenantResolver</c> and <c>ApiKeyTouchWorker</c> are
    /// provider-agnostic and registered with the collector.
    /// </summary>
    public static IServiceCollection AddSqliteApiKeyLookup(this IServiceCollection services, IConfiguration configuration, long? fixedTenantId = null)
    {
        AddSchemaStep(services, configuration, fixedTenantId);
        services.AddScoped<IApiKeyLookup, SqliteApiKeyLookup>();
        services.AddScoped<IApiKeyTouchStore, SqliteApiKeyTouchStore>();
        return services;
    }

    /// <summary>
    /// The managing side of the control plane, for the API host: alert rules, the tenant catalog and retention settings, and the same
    /// startup step as <see cref="AddSqliteApiKeyLookup"/>. <paramref name="fixedTenantId"/> (the API's <c>Telemetry:Api:FixedTenantId</c>) is
    /// created, named <c>Default</c>, when it has no row.
    /// </summary>
    public static IServiceCollection AddSqliteControlPlaneManagement(this IServiceCollection services, IConfiguration configuration, long? fixedTenantId = null)
    {
        AddSchemaStep(services, configuration, fixedTenantId);
        services.AddScoped<IAlertRuleRepository, SqliteAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, SqliteTenantCatalogRepository>();
        services.AddScoped<IRetentionSettingsRepository, SqliteRetentionSettingsRepository>();
        return services;
    }

    private static void AddSchemaStep(IServiceCollection services, IConfiguration configuration, long? fixedTenantId)
    {
        if (fixedTenantId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(fixedTenantId), fixedTenantId, "A fixed tenant id must be positive.");

        // Read now, so a missing or relative ConnectionStrings:ControlPlane fails at startup with the key named.
        services.AddSingleton(SqliteControlPlaneConnection.FromConfiguration(configuration));
        services.AddSingleton(ReadOptions(configuration));
        services.AddSingleton(new SqliteFixedTenant(fixedTenantId));

        // Ahead of every hosted service already registered, not after them: StartingAsync runs in registration order, and the
        // collector's FixedTenantStartupCheck (registered by AddKeryheTelemetryCollector, usually first) reads the tenant row this
        // step creates. Inserting here keeps that order whichever order a host registers the collector and the control plane in.
        var descriptor = ServiceDescriptor.Singleton<IHostedService, SqliteControlPlaneInitializer>();
        var firstHosted = services.ToList().FindIndex(d => d.ServiceType == typeof(IHostedService));
        if (firstHosted < 0) services.Add(descriptor);
        else services.Insert(firstHosted, descriptor);
    }

    private static SqliteControlPlaneOptions ReadOptions(IConfiguration configuration)
    {
        var options = new SqliteControlPlaneOptions();
        var key = $"{SqliteControlPlaneOptions.SectionName}:{nameof(SqliteControlPlaneOptions.ApplySchema)}";
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return options;
        if (!bool.TryParse(raw, out var apply))
            throw new InvalidOperationException($"Configuration {key} must be true or false (was '{raw}').");
        options.ApplySchema = apply;
        return options;
    }
}
