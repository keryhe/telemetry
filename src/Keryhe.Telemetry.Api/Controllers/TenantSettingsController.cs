using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Api.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

/// <summary>A tenant's settings: today its retention windows (there is no global retention default).</summary>
[ApiController]
[TenantScoped]
[Route("settings")]
public class TenantSettingsController(IRetentionSettingsRepository retention, ITenantContext tenantContext) : ControllerBase
{
    // Upper bound on any *RetentionDays field. Not a technical limit — it exists so a typo
    // (entering years instead of days) fails loudly with a 400 instead of quietly configuring
    // a multi-decade retention window.
    private const int MaxRetentionDays = 3650;

    // GET /api/tenants/{tenantId}/settings/retention
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("retention")]
    public async Task<ActionResult<RetentionSettings>> GetRetentionSettings(CancellationToken ct = default)
    {
        var settings = await retention.GetSettingsAsync(tenantContext.GetRequiredTenantId(), ct);
        return settings is null ? NotFound() : Ok(settings);
    }

    // PUT /api/tenants/{tenantId}/settings/retention
    [TelemetryOperation(TelemetryOperation.ManageSettings)]
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

        return await retention.UpdateSettingsAsync(tenantContext.GetRequiredTenantId(), settings, ct)
            ? Ok(settings)
            : NotFound();
    }

    private static bool IsValidRetentionDays(int days) => days is > 0 and <= MaxRetentionDays;
}
