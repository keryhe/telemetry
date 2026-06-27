using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[Route("api/logs")]
public class LogsController : ControllerBase
{
    private readonly ILogReadRepository _logs;

    public LogsController(ILogReadRepository logs)
    {
        _logs = logs;
    }

    // GET /api/logs?start=&end=
    [HttpGet]
    public async Task<ActionResult<IEnumerable<LogRecordModel>>> GetLogs(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        CancellationToken ct = default)
    {
        var logs = await _logs.GetLogRecordsByTimeRangeAsync(start, end, ct);
        return Ok(logs);
    }

    // GET /api/logs/services?start=&end=
    [HttpGet("services")]
    public async Task<ActionResult<List<string>>> GetDistinctServices(
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        CancellationToken ct = default)
    {
        var services = await _logs.GetDistinctServicesAsync(start, end, ct);
        return Ok(services);
    }

    // GET /api/logs/by-trace/{traceId}
    [HttpGet("by-trace/{traceId}")]
    public async Task<ActionResult<IEnumerable<LogRecordModel>>> GetLogsByTrace(
        string traceId,
        CancellationToken ct = default)
    {
        var logs = await _logs.GetLogRecordsByTraceIdAsync(traceId, ct);
        return Ok(logs);
    }
}
