using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Api.Authorization;

/// <summary>
/// Maps a <see cref="TelemetryOperation"/> to the policy configured for it (see
/// <see cref="TelemetryApiAuthorizationOptions.Policies"/> for the fallbacks). Shared by the filter, which
/// needs the policy's authentication schemes, and <see cref="PolicyMappedOperationHandler"/>, which evaluates it.
/// </summary>
public sealed class TelemetryPolicyResolver(IOptions<TelemetryApiOptions> options, IAuthorizationPolicyProvider policyProvider)
{
    public async Task<AuthorizationPolicy> ResolveAsync(TelemetryOperation operation)
    {
        var name = PolicyNameFor(options.Value.Authorization, operation);
        if (name is null) return await policyProvider.GetDefaultPolicyAsync();
        return await policyProvider.GetPolicyAsync(name)
            ?? throw new InvalidOperationException($"Authorization policy '{name}' is not registered.");
    }

    /// <summary>The configured policy name for an operation after fallbacks; null means the host's default policy.</summary>
    internal static string? PolicyNameFor(TelemetryApiAuthorizationOptions o, TelemetryOperation operation)
    {
        string? Get(string key) => o.Policies.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        return operation switch
        {
            TelemetryOperation.Read => Get("Read"),
            TelemetryOperation.Export => Get("Export") ?? Get("Read"),
            TelemetryOperation.ManageAlerts => Get("ManageAlerts") ?? Get("Admin"),
            TelemetryOperation.ManageSettings => Get("ManageSettings") ?? Get("Admin"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }
}
