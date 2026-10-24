using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Sqlite;
using Keryhe.Telemetry.Sqlite.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SQLite control-plane registration extensions. The host selects them with <c>ControlPlane:Provider = "Sqlite"</c>; the database is
/// the file named by <c>ConnectionStrings:ControlPlane</c> (<c>Data Source=&lt;absolute path&gt;</c>). SQLite is a control-plane provider
/// only, never a <c>Database:Provider</c>, and every process that opens the file must run on the same machine's local file system.
/// </summary>
public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// The API-key side of the control plane, for a host that authenticates callers by key: the key lookup and the <c>last_used_at</c>
    /// update, and a startup check that fails (before the host starts serving) when the database is missing or not at this build's schema
    /// version. It never creates or migrates the file: a host that calls <see cref="AddSqliteControlPlaneManagement"/> must have started
    /// first. <c>CachingTenantResolver</c> and <c>ApiKeyTouchWorker</c> are provider-agnostic and registered with the collector.
    /// </summary>
    public static IServiceCollection AddSqliteApiKeyLookup(this IServiceCollection services, IConfiguration configuration)
    {
        // Read now, so a missing or relative ConnectionStrings:ControlPlane fails at startup with the key named.
        services.AddSingleton(SqliteControlPlaneConnection.FromConfiguration(configuration));
        services.AddScoped<IApiKeyLookup, SqliteApiKeyLookup>();
        services.AddScoped<IApiKeyTouchStore, SqliteApiKeyTouchStore>();
        services.AddHostedService<SqliteControlPlaneStartupCheck>();
        return services;
    }

    /// <summary>
    /// The managing side of the control plane: alert rules, the tenant catalog and retention settings, and the startup step that creates
    /// the database or migrates it forward (<c>Telemetry:ControlPlane:Sqlite:ApplySchema</c>, default true; false only checks). Call it
    /// in the one host that owns the file's schema.
    /// </summary>
    public static IServiceCollection AddSqliteControlPlaneManagement(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(SqliteControlPlaneConnection.FromConfiguration(configuration));
        services.AddSingleton(ReadOptions(configuration));
        services.AddScoped<IAlertRuleRepository, SqliteAlertRuleRepository>();
        services.AddScoped<ITenantCatalogRepository, SqliteTenantCatalogRepository>();
        services.AddScoped<IRetentionSettingsRepository, SqliteRetentionSettingsRepository>();
        services.AddHostedService<SqliteControlPlaneInitializer>();
        return services;
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
