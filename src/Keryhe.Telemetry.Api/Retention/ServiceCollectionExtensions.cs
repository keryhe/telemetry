using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Keryhe.Telemetry.Api.Retention;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration for the retention subsystem. <see cref="AddRetention"/> wires up the periodic
/// <see cref="RetentionWorker"/> hosted service. Mirrors <c>AddAlerting</c>.
///
/// The caller must have already registered the telemetry provider's <c>IRetentionSweeper</c> (from
/// its <c>Add&lt;Provider&gt;ApiServices</c>) and the control plane's <c>IRetentionSettingsRepository</c>
/// (from its <c>Add&lt;Provider&gt;ControlPlaneApiServices</c>).
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

        // The rollup hour tier's compaction (plans/summary-rollups.md, Phase 4): idles unless the provider registers an
        // IRollupCompactor (MySQL). Reads RollupOptions (Telemetry:Rollup), registered by AddKeryheTelemetryApi.
        services.Configure<Keryhe.Telemetry.Core.Data.RollupOptions>(configuration.GetSection(Keryhe.Telemetry.Core.Data.RollupOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<RollupCompactionWorker>();

        return services;
    }
}
