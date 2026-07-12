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

    // GET /api/logs/search?start=&end=&service=&minSeverity=&q=&limit=&offset=
    // Server-side filtered + paged logs for the logs list page.
    [HttpGet("search")]
    public async Task<ActionResult<PagedResult<LogRecordModel>>> SearchLogs(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] string? service = null,
        [FromQuery] int? minSeverity = null,
        [FromQuery] string? q = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var result = await _logs.QueryLogRecordsAsync(new LogQuery
        {
            Start = start,
            End = end,
            Service = service,
            MinSeverity = minSeverity,
            Search = q,
            Limit = limit,
            Offset = offset
        }, ct);
        return Ok(result);
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

    // GET /api/logs/context?anchor=<timeUnixNano>&service=&before=&after=
    // The N log records before/after the anchor timestamp for the same service, ignoring list filters.
    [HttpGet("context")]
    public async Task<ActionResult<IEnumerable<LogRecordModel>>> GetContext(
        [FromQuery] long anchor,
        [FromQuery] string? service = null,
        [FromQuery] int before = 10,
        [FromQuery] int after = 10,
        CancellationToken ct = default)
    {
        var logs = await _logs.GetSurroundingLogRecordsAsync(anchor, service, before, after, ct);
        return Ok(logs);
    }
}
