using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    // Upper bound on any *RetentionDays field. Not a technical limit — it exists so a typo
    // (entering years instead of days) fails loudly with a 400 instead of quietly configuring
    // a multi-decade retention window.
    private const int MaxRetentionDays = 3650;

    private readonly IRetentionSettingsRepository _retention;

    public SettingsController(IRetentionSettingsRepository retention)
    {
        _retention = retention;
    }

    // GET /api/settings/retention
    [HttpGet("retention")]
    public async Task<ActionResult<RetentionSettings>> GetRetentionSettings(CancellationToken ct = default)
    {
        var settings = await _retention.GetSettingsAsync(ct);
        return Ok(settings);
    }

    // PUT /api/settings/retention
    [HttpPut("retention")]
    public async Task<ActionResult<RetentionSettings>> UpdateRetentionSettings(
        [FromBody] RetentionSettings settings,
        CancellationToken ct = default)
    {
        if (!IsValidRetentionDays(settings.TraceRetentionDays) ||
            !IsValidRetentionDays(settings.LogRetentionDays) ||
            !IsValidRetentionDays(settings.MetricRetentionDays))
        {
            return BadRequest($"Retention days must be a positive integer no greater than {MaxRetentionDays}.");
        }

        await _retention.UpdateSettingsAsync(settings, ct);
        return Ok(settings);
    }

    private static bool IsValidRetentionDays(int days) => days is > 0 and <= MaxRetentionDays;
}
