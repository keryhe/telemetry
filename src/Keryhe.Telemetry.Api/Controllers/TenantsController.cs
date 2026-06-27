using Keryhe.Telemetry.Core;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[Route("api/tenants")]
public class TenantsController(ITenantCatalogRepository repository) : ControllerBase
{
    // GET /api/tenants
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TenantDto>>> GetTenants(CancellationToken ct = default)
    {
        var tenants = await repository.GetAllTenantsAsync(ct);
        return Ok(tenants.Select(t => new TenantDto(t.Id, t.Name)));
    }
}

public sealed record TenantDto(long Id, string Name);
