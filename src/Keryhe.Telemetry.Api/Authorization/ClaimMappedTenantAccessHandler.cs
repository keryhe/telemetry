using System.Collections.Concurrent;
using Keryhe.Telemetry.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Api.Authorization;

/// <summary>
/// Handles <see cref="TenantAccessRequirement"/> from <c>TenantMappings</c>. The grants come from the
/// signed-in principal, the tenant from the route, so editing the URL never widens access. Tenant names
/// resolve to ids through the catalog, cached briefly so a rename is picked up.
/// </summary>
public sealed class ClaimMappedTenantAccessHandler(
    IOptions<TelemetryApiOptions> options,
    ITenantCatalogRepository catalog,
    IMemoryCache cache,
    ILogger<ClaimMappedTenantAccessHandler> logger) : AuthorizationHandler<TenantAccessRequirement, TelemetryResource>
{
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, byte> LoggedUnknown = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TenantAccessRequirement requirement, TelemetryResource resource)
    {
        if (resource.TenantId is not { } tenantId) return;

        var granted = options.Value.Authorization.TenantMappings
            .Where(m => context.User.HasClaim(c => c.Type == m.ClaimType && c.Value == m.ClaimValue))
            .SelectMany(m => m.Tenants)
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (granted.Count == 0) return;

        if (granted.Contains("*")) { context.Succeed(requirement); return; }
        if (granted.Any(t => long.TryParse(t, out var id) && id == tenantId)) { context.Succeed(requirement); return; }

        var names = granted.Where(t => !long.TryParse(t, out _)).ToList();
        if (names.Count == 0) return;

        var tenants = await GetCatalogAsync();
        foreach (var name in names)
        {
            var match = tenants.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                if (LoggedUnknown.TryAdd(name, 0))
                    logger.LogWarning("Telemetry:Api:Authorization:TenantMappings names tenant '{Tenant}', which does not exist.", name);
            }
            else if (match.Id == tenantId)
            {
                context.Succeed(requirement);
                return;
            }
        }
    }

    private async Task<List<TenantInfo>> GetCatalogAsync() =>
        (await cache.GetOrCreateAsync("keryhe.telemetry.tenant-catalog", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CatalogTtl;
            return await catalog.GetAllTenantsAsync();
        }))!;
}
