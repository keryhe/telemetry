using Keryhe.Telemetry.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Api.Authorization;

/// <summary>
/// Added to this library's controllers only. Order: authenticate the operation policy's own schemes (if it
/// names any), authorize the action's operation and, on a tenant-scoped route, the tenant (when enabled);
/// apply the CSRF rule; then set the request's tenant from the route. Unauthenticated failures challenge
/// (401), authenticated ones forbid (403), through the policy's schemes when it names any.
/// </summary>
public sealed class TelemetryAuthorizationFilter(
    IOptions<TelemetryApiOptions> options,
    IAuthorizationService authorization,
    IPolicyEvaluator policyEvaluator,
    TelemetryPolicyResolver policies,
    ApiTenantContext tenantContext) : IAsyncAuthorizationFilter
{
    public const string ClientHeader = "X-Telemetry-Client";

    /// <summary>
    /// Set to <c>tenant</c> on a 403 refused by the tenant check, so a client can tell "no access to this
    /// tenant" from "not allowed to do this" (an operation refusal carries no such header). A UI on another
    /// origin needs it in the CORS policy's exposed headers.
    /// </summary>
    public const string DeniedHeader = "X-Telemetry-Denied";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        long? tenantId = null;
        if (context.RouteData.Values.TryGetValue("tenantId", out var raw)
            && long.TryParse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture), out var parsed)
            && parsed > 0)
        {
            tenantId = parsed;
        }

        if (options.Value.Authorization.Enabled)
        {
            var operation = (context.ActionDescriptor as ControllerActionDescriptor)?.MethodInfo
                .GetCustomAttributes(typeof(TelemetryOperationAttribute), false)
                .Cast<TelemetryOperationAttribute>().SingleOrDefault()?.Operation
                ?? throw new InvalidOperationException(
                    $"Action '{context.ActionDescriptor.DisplayName}' has no [TelemetryOperation]; refusing to serve it unauthorized.");

            // As ASP.NET's own authorization middleware does: a policy that names schemes is evaluated against
            // the principal those schemes produce, not only the default scheme's. AuthenticateAsync replaces
            // HttpContext.User with it, so the tenant check and the action see the same identity.
            var policy = await policies.ResolveAsync(operation);
            if (policy.AuthenticationSchemes.Count > 0)
                await policyEvaluator.AuthenticateAsync(policy, http);
            var schemes = policy.AuthenticationSchemes.ToArray();

            var resource = new TelemetryResource(tenantId);
            if (!(await authorization.AuthorizeAsync(http.User, resource, new TelemetryOperationRequirement(operation))).Succeeded)
            {
                context.Result = Refuse(http, schemes);
                return;
            }

            if (tenantId is not null
                && !(await authorization.AuthorizeAsync(http.User, resource, new TenantAccessRequirement())).Succeeded)
            {
                http.Response.Headers[DeniedHeader] = "tenant";
                context.Result = Refuse(http, schemes);
                return;
            }

            // A cross-site form cannot set a custom header, and a cross-origin fetch with one needs a
            // preflight. Only a bearer token exempts: a browser never attaches one on its own, whereas it
            // does attach Basic and Negotiate/NTLM credentials, so those are no proof of a script.
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
                && !HttpMethods.IsOptions(http.Request.Method) && !HttpMethods.IsTrace(http.Request.Method)
                && !HasBearerToken(http.Request)
                && !http.Request.Headers.ContainsKey(ClientHeader))
            {
                context.Result = new BadRequestObjectResult(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = $"Requests that change data without a bearer token must send {ClientHeader}: 1.",
                });
                return;
            }
        }

        if (tenantId is { } id) tenantContext.SetTenantId(id);
    }

    private static IActionResult Refuse(HttpContext http, string[] schemes) =>
        http.User.Identity?.IsAuthenticated == true ? new ForbidResult(schemes) : new ChallengeResult(schemes);

    private static bool HasBearerToken(HttpRequest request) =>
        request.Headers.Authorization.Any(v => v is not null && v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase));
}
