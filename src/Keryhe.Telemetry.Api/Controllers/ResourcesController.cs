using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Api.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[TenantScoped]
[Route("resources")]
public class ResourcesController : ControllerBase
{
    private readonly IResourceReadRepository _resources;

    public ResourcesController(IResourceReadRepository resources)
    {
        _resources = resources;
    }

    // GET /api/tenants/{tenantId}/resources/services
    // All distinct service.name values for the current tenant's resources, regardless of
    // signal type or time range — the single authoritative "available services" list.
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("services")]
    public async Task<ActionResult<List<string>>> GetDistinctServices(CancellationToken ct = default)
    {
        var services = await _resources.GetDistinctServicesAsync(ct);
        return Ok(services);
    }
}
