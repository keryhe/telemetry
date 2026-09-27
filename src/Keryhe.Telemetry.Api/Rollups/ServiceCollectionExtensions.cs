using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Api.Rollups;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration for the rollup subsystem. <see cref="AddRollups"/> wires up the periodic
/// <see cref="RollupWorker"/> hosted service. Mirrors <c>AddRetention</c>.
///
/// The caller must have already registered the active provider's <c>IRollupRepository</c> — in
/// the API host this comes from <c>AddKeryheTelemetryApi</c>'s provider registration.
/// </summary>
public static class RollupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the rollup sweep and the background worker that drives it.
    /// </summary>
    /// <param name="configuration">Host configuration; options bind from the <c>Telemetry:Rollups</c> section.</param>
    /// <param name="configure">Optional overrides applied after configuration binding.</param>
    public static IServiceCollection AddRollups(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<RollupOptions>? configure = null)
    {
        services.Configure<RollupOptions>(configuration.GetSection(RollupOptions.SectionName));
        if (configure is not null)
            services.Configure(configure);

        services.AddHostedService<RollupWorker>();

        return services;
    }
}
