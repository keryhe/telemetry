using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Keryhe.Telemetry.Api.Authorization;

/// <summary>
/// Handles <see cref="TelemetryOperationRequirement"/> by evaluating the policy configured for the
/// operation (see <see cref="TelemetryApiAuthorizationOptions.Policies"/> for the fallbacks). The policy's
/// authentication schemes have already been authenticated by <see cref="TelemetryAuthorizationFilter"/>.
/// </summary>
public sealed class PolicyMappedOperationHandler(TelemetryPolicyResolver policies, IServiceProvider services)
    : AuthorizationHandler<TelemetryOperationRequirement, TelemetryResource>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TelemetryOperationRequirement requirement, TelemetryResource resource)
    {
        var policy = await policies.ResolveAsync(requirement.Operation);
        // Resolved lazily: IAuthorizationService depends on every handler, this one included.
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(context.User, resource, policy);
        if (result.Succeeded) context.Succeed(requirement);
    }
}
