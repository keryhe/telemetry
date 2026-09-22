using Keryhe.Telemetry.Ui;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service registration for the packaged Angular UI, the companion to
/// <see cref="TelemetryUiApplicationBuilderExtensions.UseKeryheTelemetryUi"/> — the same
/// <c>Add*</c>/<c>Use*</c> split as <c>AddRetention</c> and <c>AddAlerting</c> on the API side.
/// </summary>
public static class TelemetryUiServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="TelemetryUiOptions"/> from the <c>TelemetryUi</c> configuration section and
    /// registers the in-memory SPA shell.
    ///
    /// Required: <c>UseKeryheTelemetryUi()</c> throws without it. The registration exists so that
    /// the UI's settings — where it is mounted, where its API is, what it is called — can come from
    /// <c>appsettings.json</c>, an environment variable or a command-line switch. That matters more
    /// here than for most options types: this UI ships prebuilt precisely so a consumer never has to
    /// rebuild it, and a setting reachable only from C# would put them back to editing and
    /// recompiling a host to relocate a static bundle.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration to bind the <c>TelemetryUi</c> section from.</param>
    /// <param name="configure">
    /// Optional overrides applied after configuration binding. Effective precedence is configuration
    /// section, then this delegate, then any delegate passed to <c>UseKeryheTelemetryUi</c>.
    /// </param>
    public static IServiceCollection AddKeryheTelemetryUi(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TelemetryUiOptions>? configure = null)
    {
        services.Configure<TelemetryUiOptions>(configuration.GetSection(TelemetryUiOptions.SectionName));
        if (configure is not null)
            services.Configure(configure);

        services.AddSingleton<TelemetryUiShell>();

        return services;
    }
}
