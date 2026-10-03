using Keryhe.Telemetry.Api.Authorization;
using Keryhe.Telemetry.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[Route("tenants")]
public class TenantsController(
    ITenantCatalogRepository repository,
    IAuthorizationService authorization,
    IOptions<TelemetryApiOptions> options) : ControllerBase
{
    // GET /api/tenants
    // With authorization enabled, only the tenants the caller may access (the same per-tenant check
    // the tenant-scoped routes apply).
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TenantDto>>> GetTenants(CancellationToken ct = default)
    {
        var tenants = await repository.GetAllTenantsAsync(ct);
        if (options.Value.Authorization.Enabled)
        {
            var requirement = new TenantAccessRequirement();
            var permitted = new List<TenantInfo>();
            foreach (var t in tenants)
            {
                if ((await authorization.AuthorizeAsync(User, new TelemetryResource(t.Id), requirement)).Succeeded)
                    permitted.Add(t);
            }
            tenants = permitted;
        }

        return Ok(tenants.Select(t => new TenantDto(t.Id, t.Name)));
    }
}

public sealed record TenantDto(long Id, string Name);
