using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Api.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Keryhe.Telemetry.Api.Controllers;

/// <summary>
/// Exposes the active provider's <see cref="ProviderCapabilities"/> (list-pages-server-side plan,
/// Phase 1, decision 40) so the UI can explain a limit ("Search is limited to 24 hours on SQL
/// Server") instead of the request just being slower or erroring unexplained. Fetched once by the
/// client at startup.
/// </summary>
[ApiController]
[Route("capabilities")]
public class CapabilitiesController(ProviderCapabilities capabilities, IConfiguration configuration) : ControllerBase
{
    // GET /api/capabilities
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet]
    public ActionResult<CapabilitiesDto> GetCapabilities()
    {
        // Database:Provider is the same config key every host switches on to select a provider
        // (see CLAUDE.md's "Provider abstraction" section); ProviderCapabilities itself carries no
        // provider name, so this is read straight from configuration rather than threading a name
        // through the registration call in all five providers.
        var provider = configuration["Database:Provider"] ?? "Unknown";
        return Ok(new CapabilitiesDto(
            provider,
            capabilities.RawSearchWindowHours,
            capabilities.ExportMaxWindowDays,
            capabilities.Limits.Logs,
            capabilities.Limits.Traces,
            capabilities.Limits.MetricCatalog,
            capabilities.Limits.Exemplars));
    }
}

public sealed record CapabilitiesDto(
    string Provider,
    int? RawSearchWindowHours,
    int ExportMaxWindowDays,
    int LogListLimit,
    int TraceListLimit,
    int MetricCatalogLimit,
    int ExemplarLimit);
