using System.Text.Json;
using Keryhe.Telemetry.Api.Export;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[Route("api/logs")]
public class LogsController : ControllerBase
{
    private readonly ILogReadRepository _logs;
    private readonly ProviderCapabilities _capabilities;
    private readonly ExportConcurrencyGate _exportGate;

    public LogsController(ILogReadRepository logs, ProviderCapabilities capabilities, ExportConcurrencyGate exportGate)
    {
        _logs = logs;
        _capabilities = capabilities;
        _exportGate = exportGate;
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

    /// <summary>
    /// GET /api/logs/export?start=&end=&service=&minSeverity=&q=&format=ndjson|csv
    /// Phase 8 (list-pages-server-side plan, decision 17): full records, same filters as
    /// <see cref="GetSummary"/>/<see cref="GetPage"/>, streamed with no row cap. Bounded to
    /// <see cref="ProviderCapabilities.ExportMaxWindowDays"/> (400 beyond it) and
    /// <see cref="ExportConcurrencyGate"/>'s slot count (429 when exhausted). <c>ct</c> is bound by
    /// MVC to <c>HttpContext.RequestAborted</c>, which is what propagates a client disconnect down
    /// into the repository's own cancellation-aware read.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> GetExport(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] string? service = null,
        [FromQuery] int? minSeverity = null,
        [FromQuery] string? q = null,
        [FromQuery] string format = "ndjson",
        CancellationToken ct = default)
    {
        if (start >= end)
            return BadRequest("Start time must be before end time.");
        if (!ExportFormatParser.TryParse(format, out var exportFormat))
            return BadRequest("format must be 'ndjson' or 'csv'.");

        var windowGuard = ExportWindowGuard.Check(_capabilities, end - start);
        if (!windowGuard.Allowed)
            return BadRequest(windowGuard.Message);

        using var slot = _exportGate.TryEnter();
        if (slot == null)
            return StatusCode(StatusCodes.Status429TooManyRequests, "Too many exports are already running on this API instance. Try again shortly.");

        var query = new LogExportQuery { Start = start, End = end, Service = service, MinSeverity = minSeverity, Search = q };

        Response.ContentType = exportFormat.ContentType();
        Response.Headers.ContentDisposition = $"attachment; filename=\"logs-export.{exportFormat.FileExtension()}\"";

        var rowCount = 0;
        if (exportFormat == ExportFormat.Csv)
        {
            await using var csv = new CsvRowWriter(Response.Body);
            await csv.WriteHeaderAsync(["timestamp", "severityText", "severityNumber", "serviceName", "traceId", "spanId", "eventName", "body", "attributes"]);
            await foreach (var log in _logs.ExportLogsAsync(query, ct))
            {
                await csv.WriteRowAsync(LogCsvCells(log));
                if (++rowCount % 500 == 0)
                    await csv.FlushAsync();
            }
        }
        else
        {
            await foreach (var log in _logs.ExportLogsAsync(query, ct))
            {
                await NdjsonRowWriter.WriteLineAsync(Response.Body, log, ct);
                if (++rowCount % 500 == 0)
                    await Response.Body.FlushAsync(ct);
            }
        }

        await Response.Body.FlushAsync(ct);
        return new EmptyResult();
    }

    private static IEnumerable<string?> LogCsvCells(LogRecordModel log)
    {
        var timestamp = log.TimeUnixNano.HasValue ? TimeConversion.UnixNanoToDateTime(log.TimeUnixNano.Value).ToString("O") : "";
        var serviceName = log.Resource?.Attributes != null && log.Resource.Attributes.TryGetValue("service.name", out var s) ? s?.ToString() : null;
        yield return timestamp;
        yield return log.SeverityText;
        yield return log.SeverityNumber?.ToString();
        yield return serviceName;
        yield return log.TraceIdHex;
        yield return log.SpanIdHex;
        yield return log.EventName;
        yield return log.BodyValue;
        yield return JsonSerializer.Serialize(log.Attributes);
    }
}
