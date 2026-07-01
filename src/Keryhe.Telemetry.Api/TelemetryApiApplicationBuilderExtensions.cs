using Keryhe.Telemetry.Api.Middleware;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Middleware pipeline extensions for the Keryhe Telemetry API.
/// </summary>
public static class TelemetryApiApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the tenant-resolution middleware. Place after <c>UseCors</c> so preflight
    /// requests pass through. The host still owns <c>MapControllers()</c>.
    /// </summary>
    public static IApplicationBuilder UseKeryheTelemetryApi(this IApplicationBuilder app)
    {
        app.UseMiddleware<TenantMiddleware>();
        return app;
    }
}
