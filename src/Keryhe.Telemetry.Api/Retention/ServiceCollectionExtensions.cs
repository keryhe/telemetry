using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Api.Retention;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration for the retention subsystem. <see cref="AddRetention"/> wires up the periodic
/// <see cref="RetentionWorker"/> hosted service. Mirrors <c>AddAlerting</c>.
///
/// The caller must have already registered the active provider's
/// <c>IRetentionSettingsRepository</c> — in the API host this comes from
/// <c>AddKeryheTelemetryApi</c>.
/// </summary>
public static class RetentionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the retention sweep and the background worker that drives it.
    /// </summary>
    /// <param name="configuration">Host configuration; options bind from the
    /// <c>Retention</c> section.</param>
    /// <param name="configure">Optional overrides applied after configuration binding.</param>
    public static IServiceCollection AddRetention(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<RetentionOptions>? configure = null)
    {
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
        if (configure is not null)
            services.Configure(configure);

        services.AddHostedService<RetentionWorker>();

        return services;
    }
}
