using Keryhe.Telemetry.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Pipeline extensions for the Keryhe Telemetry API.
/// </summary>
public static class TelemetryApiApplicationBuilderExtensions
{
    /// <summary>
    /// Formerly added the tenant middleware. The tenant now comes from the route and is resolved by a
    /// filter on the API's own controllers, so there is nothing left to add to the pipeline.
    /// </summary>
    [Obsolete("The tenant is read from the route; this is a no-op. Remove the call and use MapKeryheTelemetryApi().")]
    public static IApplicationBuilder UseKeryheTelemetryApi(this IApplicationBuilder app) => app;

    /// <summary>
    /// Maps the controllers (the API's and the host's own) and answers any other path under the API
    /// base path, including a tenant segment that is not a positive number, with a JSON 404 instead of
    /// letting it fall through to the UI's SPA shell. Call after <c>UseAuthentication()</c> and
    /// <c>UseAuthorization()</c>. The library authorizes its own actions; do not call
    /// <c>RequireAuthorization()</c> on the returned builder, which would also lock the host's controllers.
    /// </summary>
    public static ControllerActionEndpointConventionBuilder MapKeryheTelemetryApi(this IEndpointRouteBuilder endpoints)
    {
        var basePath = endpoints.ServiceProvider.GetRequiredService<IOptions<TelemetryApiOptions>>().Value.NormalizedBasePath;
        endpoints.MapFallback(basePath + "/{**rest}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such API route."));
        return endpoints.MapControllers();
    }
}
