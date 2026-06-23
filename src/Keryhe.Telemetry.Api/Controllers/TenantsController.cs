using Keryhe.Telemetry.Data.Access;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[Route("api/tenants")]
public class TenantsController : ControllerBase
{
    private readonly IDbContextFactory<TelemetryReadDbContext> _contextFactory;

    public TenantsController(IDbContextFactory<TelemetryReadDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    // GET /api/tenants
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TenantDto>>> GetTenants(CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var tenants = await context.Tenants
            .OrderBy(t => t.Name)
            .Select(t => new TenantDto(t.Id, t.Name))
            .ToListAsync(ct);

        return Ok(tenants);
    }
}

public sealed record TenantDto(long Id, string Name);
