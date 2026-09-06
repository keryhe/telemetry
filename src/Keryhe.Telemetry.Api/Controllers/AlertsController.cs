using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[Route("api/alerts")]
public class AlertsController : ControllerBase
{
    private readonly IAlertRuleRepository _alerts;

    public AlertsController(IAlertRuleRepository alerts)
    {
        _alerts = alerts;
    }

    // GET /api/alerts/rules
    [HttpGet("rules")]
    public async Task<ActionResult<List<AlertRule>>> GetAllRules(CancellationToken ct = default)
    {
        var rules = await _alerts.GetAllRulesAsync(ct);
        return Ok(rules);
    }

    // POST /api/alerts/rules
    [HttpPost("rules")]
    public async Task<ActionResult<AlertRule>> CreateRule(
        [FromBody] AlertRule rule,
        CancellationToken ct = default)
    {
        var created = await _alerts.CreateRuleAsync(rule, ct);
        return CreatedAtAction(nameof(GetAllRules), created);
    }

    // PUT /api/alerts/rules/{id}
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

    // DELETE /api/alerts/rules/{id}
    [HttpDelete("rules/{id:int}")]
    public async Task<IActionResult> DeleteRule(int id, CancellationToken ct = default)
    {
        await _alerts.DeleteRuleAsync(id, ct);
        return NoContent();
    }

    // GET /api/alerts/events?limit=50
    [HttpGet("events")]
    public async Task<ActionResult<List<AlertEvent>>> GetRecentEvents(
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var events = await _alerts.GetRecentAlertEventsAsync(limit, ct);
        return Ok(events);
    }
}
