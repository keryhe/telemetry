using Keryhe.Telemetry.Core;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[Route("api/resources")]
public class ResourcesController : ControllerBase
{
    private readonly IResourceReadRepository _resources;

    public ResourcesController(IResourceReadRepository resources)
    {
        _resources = resources;
    }

    // GET /api/resources/services
    // All distinct service.name values for the current tenant's resources, regardless of
    // signal type or time range — the single authoritative "available services" list.
    [HttpGet("services")]
    public async Task<ActionResult<List<string>>> GetDistinctServices(CancellationToken ct = default)
    {
        var services = await _resources.GetDistinctServicesAsync(ct);
        return Ok(services);
    }
}
