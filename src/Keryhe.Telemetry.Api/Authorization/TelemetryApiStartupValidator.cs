using Keryhe.Telemetry.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Api.Authorization;

/// <summary>
/// Fails startup when authorization is enabled but cannot work (undefined policy, no authentication
/// scheme, malformed mappings, nothing able to grant tenant access), and warns about the settings that
/// are legal but surprising.
/// </summary>
internal sealed class TelemetryApiStartupValidator(
    IOptions<TelemetryApiOptions> options,
    IAuthorizationPolicyProvider policyProvider,
    IAuthenticationSchemeProvider schemes,
    IServiceScopeFactory scopes,
    IHostEnvironment environment,
    ILogger<TelemetryApiStartupValidator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var auth = options.Value.Authorization;
        if (!auth.Enabled)
        {
            if (!environment.IsDevelopment())
                logger.LogWarning("The telemetry API is unauthenticated ({Section}:Authorization:Enabled is false): anyone who can reach it can read every tenant and change settings and alert rules.", TelemetryApiOptions.SectionName);
            return;
        }

        var problems = new List<string>();

        foreach (var (key, name) in auth.Policies)
        {
            if (!Enum.TryParse<PolicyKey>(key, true, out _))
                problems.Add($"Authorization:Policies:{key} is not a known level (Read, Admin, Export, ManageAlerts, ManageSettings).");
            else if (await policyProvider.GetPolicyAsync(name) is null)
                problems.Add($"Authorization:Policies:{key} names policy '{name}', which is not registered.");
        }

        // The collector's API-key scheme is not an API scheme (a co-hosted collector must not hide a missing AddAuthentication).
        if (!(await schemes.GetAllSchemesAsync()).Any(x => x.Name != TelemetryAuthenticationSchemes.ApiKey))
            problems.Add("no authentication scheme is registered (AddAuthentication(...)).");

        for (var i = 0; i < auth.TenantMappings.Count; i++)
        {
            var m = auth.TenantMappings[i];
            if (string.IsNullOrWhiteSpace(m.ClaimType) || string.IsNullOrWhiteSpace(m.ClaimValue) || m.Tenants.Count == 0
                || m.Tenants.Any(string.IsNullOrWhiteSpace))
                problems.Add($"Authorization:TenantMappings:{i} needs a ClaimType, a ClaimValue and at least one tenant.");
        }

        if (auth.TenantMappings.Count == 0 && !HasConsumerHandler())
            problems.Add("no tenant access is configured: set Authorization:TenantMappings or register an " +
                         "AuthorizationHandler<TenantAccessRequirement, TelemetryResource>, otherwise every tenant route is refused.");

        if (problems.Count > 0)
            throw new InvalidOperationException($"{TelemetryApiOptions.SectionName} authorization is misconfigured: " + string.Join(" ", problems));

        if (TelemetryPolicyResolver.PolicyNameFor(auth, TelemetryOperation.ManageSettings) is null)
            logger.LogWarning("No Admin policy is configured, so every signed-in user (the default policy) can change settings and alert rules. Set {Section}:Authorization:Policies:Admin.", TelemetryApiOptions.SectionName);
    }

    // Handlers are scoped (they use scoped repositories), so look at them through a scope.
    private bool HasConsumerHandler()
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetServices<IAuthorizationHandler>()
            .Any(h => h is not (PolicyMappedOperationHandler or ClaimMappedTenantAccessHandler)
                      && h.GetType().Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) != true);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private enum PolicyKey { Read, Admin, Export, ManageAlerts, ManageSettings }
}
