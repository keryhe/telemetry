using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Api.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[TenantScoped]
[Route("alerts")]
public class AlertsController : ControllerBase
{
    private readonly IAlertRuleRepository _alerts;

    public AlertsController(IAlertRuleRepository alerts)
    {
        _alerts = alerts;
    }

    // GET /api/tenants/{tenantId}/alerts/rules
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("rules")]
    public async Task<ActionResult<List<AlertRule>>> GetAllRules(CancellationToken ct = default)
    {
        var rules = await _alerts.GetAllRulesAsync(ct);
        return Ok(rules);
    }

    // POST /api/tenants/{tenantId}/alerts/rules
    [TelemetryOperation(TelemetryOperation.ManageAlerts)]
    [HttpPost("rules")]
    public async Task<ActionResult<AlertRule>> CreateRule(
        [FromBody] AlertRule rule,
        CancellationToken ct = default)
    {
        var created = await _alerts.CreateRuleAsync(rule, ct);
        return CreatedAtAction(nameof(GetAllRules), created);
    }

    // PUT /api/tenants/{tenantId}/alerts/rules/{id}
    [TelemetryOperation(TelemetryOperation.ManageAlerts)]
    [HttpPut("rules/{id:int}")]
    public async Task<ActionResult<AlertRule>> UpdateRule(
        int id,
        [FromBody] AlertRule rule,
        CancellationToken ct = default)
    {
        rule.Id = id;
        var updated = await _alerts.UpdateRuleAsync(rule, ct);
        return Ok(updated);
    }

    // DELETE /api/tenants/{tenantId}/alerts/rules/{id}
    [TelemetryOperation(TelemetryOperation.ManageAlerts)]
    [HttpDelete("rules/{id:int}")]
    public async Task<IActionResult> DeleteRule(int id, CancellationToken ct = default)
    {
        await _alerts.DeleteRuleAsync(id, ct);
        return NoContent();
    }

    // GET /api/tenants/{tenantId}/alerts/events?limit=50
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("events")]
    public async Task<ActionResult<List<AlertEvent>>> GetRecentEvents(
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var events = await _alerts.GetRecentAlertEventsAsync(limit, ct);
        return Ok(events);
    }
}
