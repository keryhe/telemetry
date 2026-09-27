using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[Route("api/logs")]
public class LogsController : ControllerBase
{
    private readonly ILogReadRepository _logs;
    private readonly ProviderCapabilities _capabilities;

    public LogsController(ILogReadRepository logs, ProviderCapabilities capabilities)
    {
        _logs = logs;
        _capabilities = capabilities;
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

    // GET /api/logs/summary?start=&end=&asOf=&service=&minSeverity=&q=&bucketCount=
    [HttpGet("summary")]
    public async Task<ActionResult<LogSummaryResult>> GetSummary(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] DateTime? asOf = null,
        [FromQuery] string? service = null,
        [FromQuery] int? minSeverity = null,
        [FromQuery] string? q = null,
        [FromQuery] int bucketCount = 60,
        CancellationToken ct = default)
    {
        if (start >= end)
            return BadRequest("Start time must be before end time.");

        var guard = CheckRawSearchWindow(q, start, end);
        if (!guard.Allowed)
            return BadRequest(guard.Message);

        var result = await _logs.GetLogSummaryAsync(new LogSummaryQuery
        {
            Start = start,
            End = end,
            Service = service,
            MinSeverity = minSeverity,
            Search = q,
            AsOf = asOf,
            BucketCount = bucketCount
        }, ct);
        return Ok(result);
    }

    // GET /api/logs/page?start=&end=&asOf=&service=&minSeverity=&q=&size=&cursor=&nav=
    [HttpGet("page")]
    public async Task<ActionResult<LogPageResult>> GetPage(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] DateTime? asOf = null,
        [FromQuery] string? service = null,
        [FromQuery] int? minSeverity = null,
        [FromQuery] string? q = null,
        [FromQuery] int size = 100,
        [FromQuery] string? cursor = null,
        [FromQuery] string nav = "first",
        CancellationToken ct = default)
    {
        if (start >= end)
            return BadRequest("Start time must be before end time.");

        var guard = CheckRawSearchWindow(q, start, end);
        if (!guard.Allowed)
            return BadRequest(guard.Message);

        try
        {
            var result = await _logs.GetLogPageAsync(new LogQuery
            {
                Start = start,
                End = end,
                Service = service,
                MinSeverity = minSeverity,
                Search = q,
                Size = size,
                Cursor = cursor,
                Nav = nav,
                AsOf = asOf
            }, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // GET /api/logs/facets?start=&end=&service=&minSeverity=&q=&keys=&valueLimit=
    [HttpGet("facets")]
    public async Task<ActionResult<LogFacetsResult>> GetFacets(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] string? service = null,
        [FromQuery] int? minSeverity = null,
        [FromQuery] string? q = null,
        [FromQuery] string? keys = null,
        [FromQuery] int valueLimit = 10,
        CancellationToken ct = default)
    {
        if (start >= end)
            return BadRequest("Start time must be before end time.");

        var guard = CheckRawSearchWindow(q, start, end);
        if (!guard.Allowed)
            return BadRequest(guard.Message);

        var result = await _logs.GetLogFacetsAsync(new LogFacetsQuery
        {
            Start = start,
            End = end,
            Service = service,
            MinSeverity = minSeverity,
            Search = q,
            Keys = string.IsNullOrWhiteSpace(keys) ? null : keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ValueLimit = valueLimit
        }, ct);
        return Ok(result);
    }

    /// <summary>
    /// Decision 39's standard-tier raw-search-window guard. A trace-id-shaped <c>q</c> is exempt
    /// (indexed lookup); any other non-empty <c>q</c> counts as a raw search filter. <c>mode=slow</c>
    /// has no logs equivalent, so that exemption/trigger doesn't apply here.
    /// </summary>
    private RawSearchWindowGuard.Result CheckRawSearchWindow(string? q, DateTime start, DateTime end)
    {
        var parsed = SearchQueryParser.Parse(q);
        var hasRawSearchFilter = parsed.Terms.Count > 0;
        var isExempt = parsed.IsTraceIdSearch;
        return RawSearchWindowGuard.Check(_capabilities, hasRawSearchFilter, isExempt, end - start);
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
