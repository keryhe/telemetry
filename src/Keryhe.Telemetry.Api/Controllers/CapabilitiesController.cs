using Keryhe.Telemetry.Core;
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
[Route("api/capabilities")]
public class CapabilitiesController(ProviderCapabilities capabilities, IConfiguration configuration) : ControllerBase
{
    // GET /api/capabilities
    [HttpGet]
    public ActionResult<CapabilitiesDto> GetCapabilities()
    {
        // Database:Provider is the same config key every host switches on to select a provider
        // (see CLAUDE.md's "Provider abstraction" section); ProviderCapabilities itself carries no
        // provider name, only its Tier, so this is read straight from configuration rather than
        // threading a name through the registration call in all five providers.
        var provider = configuration["Database:Provider"] ?? "Unknown";
        return Ok(new CapabilitiesDto(
            provider,
            capabilities.Tier.ToString(),
            capabilities.IndexedSearch,
            capabilities.ExemplarPaging,
            capabilities.RawSearchWindowHours,
            capabilities.ExportMaxWindowDays,
            capabilities.AsOfBackoffSeconds));
    }
}

public sealed record CapabilitiesDto(
    string Provider,
    string Tier,
    bool IndexedSearch,
    bool ExemplarPaging,
    int? RawSearchWindowHours,
    int ExportMaxWindowDays,
    int AsOfBackoffSeconds);
