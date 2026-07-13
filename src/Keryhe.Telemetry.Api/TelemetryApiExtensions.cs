using Keryhe.Telemetry.Api;
using Keryhe.Telemetry.Api.Services;
using Keryhe.Telemetry.Core;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods that register the Keryhe Telemetry API (controllers, tenant
/// context, and the active provider's read services) into a host application.
/// </summary>
public static class TelemetryApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the telemetry API controllers and services. The host is responsible
    /// for CORS, Swagger, HTTPS redirection, and calling <c>MapControllers()</c>.
    /// </summary>
    /// <param name="configuration">
    /// Host configuration. The provider defaults to the <c>Database:Provider</c> key
    /// and connection strings are read from <c>ConnectionStrings:Read</c>.
    /// </param>
    /// <param name="configure">Optional overrides for <see cref="TelemetryApiOptions"/>.</param>
    public static IServiceCollection AddKeryheTelemetryApi(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TelemetryApiOptions>? configure = null)
    {
        var options = new TelemetryApiOptions
        {
            Provider = configuration["Database:Provider"]
        };
        configure?.Invoke(options);

        // Controllers live in this class library, so the host will not discover them
        // unless this assembly is registered as an MVC application part.
        services.AddControllers()
            .AddApplicationPart(typeof(TelemetryApiServiceCollectionExtensions).Assembly);

        // Scoped: one ApiTenantContext per request; TenantMiddleware sets the tenant id.
        services.AddScoped<ApiTenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<ApiTenantContext>());

        // Read path: the active provider's Dapper read/alert repositories are selected by
        // the resolved provider (connection string comes from ConnectionStrings:Read).
        switch (options.Provider)
        {
            case "SqlServer":  services.AddSqlServerReadServices(configuration);  break;
            case "PostgreSQL": services.AddPostgreSqlReadServices(configuration); break;
            case "Timescale":  services.AddTimescaleReadServices(configuration);  break;
            case "ClickHouse": services.AddClickHouseReadServices(configuration); break;
            default: throw new InvalidOperationException(
                "Unknown or missing telemetry database provider (expected SqlServer, PostgreSQL, Timescale, or ClickHouse).");
        }

        return services;
    }
}
