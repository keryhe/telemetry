using System.Text.Json;
using Keryhe.Telemetry.Api.Export;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Http;
using Keryhe.Telemetry.Api.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[TenantScoped]
[Route("logs")]
public class LogsController : ControllerBase
{
    private readonly ILogReadRepository _logs;
    private readonly ProviderCapabilities _capabilities;
    private readonly ExportConcurrencyGate _exportGate;
    private readonly IRollupReadRepository _rollups;
    private readonly RollupOptions _rollupOptions;
    private readonly TimeProvider _time;

    public LogsController(ILogReadRepository logs, ProviderCapabilities capabilities, ExportConcurrencyGate exportGate,
        IRollupReadRepository rollups, IOptions<RollupOptions> rollupOptions, TimeProvider time)
    {
        _rollups = rollups;
        _rollupOptions = rollupOptions.Value;
        _time = time;
        _logs = logs;
        _capabilities = capabilities;
        _exportGate = exportGate;
    }

    // GET /api/tenants/{tenantId}/logs/summary?start=&end=&service=&minSeverity=&bucketCount=
    // Counts from the log rollup: range, service and minimum severity; search does not apply (plans/summary-rollups.md).
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("summary")]
    public async Task<ActionResult<LogRollupSummaryResult>> GetSummary(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] string? service = null,
        [FromQuery] int? minSeverity = null,
        [FromQuery] int bucketCount = 60,
        CancellationToken ct = default)
    {
        if (start >= end)
            return BadRequest("Start time must be before end time.");

        var writtenThrough = RollupSummaryBuilder.WrittenThrough(_time.GetUtcNow().UtcDateTime, _rollupOptions);
        var window = RollupSummaryBuilder.PlanWindow(start, end, writtenThrough, Math.Clamp(bucketCount, 1, 200));
        if (window.IsEmpty)
            return Ok(RollupSummaryBuilder.BuildLogSummary(window, [], writtenThrough));

        var read = await _rollups.GetLogRollupAsync(new RollupQuery
        {
            StartNano = window.StartNano,
            EndNano = window.EndNano,
            BucketSeconds = window.BucketSeconds,
            Service = service,
            MinSeverity = minSeverity
        }, ct);
        return Ok(RollupSummaryBuilder.BuildLogSummary(window, read.Rows.ToList(), writtenThrough, read.TimedOut));
    }

    // GET /api/tenants/{tenantId}/logs/list?start=&end=&service=&minSeverity=&q=&order=newest|oldest&limit=
    // At most `limit` rows (clamped to Telemetry:Query:Limits:Logs) from the newest or oldest end of the window; `truncated` says more matched.
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("list")]
    public async Task<ActionResult<LogListResult>> GetList(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] string? service = null,
        [FromQuery] int? minSeverity = null,
        [FromQuery] string? q = null,
        [FromQuery] string order = ListOrder.Newest,
        [FromQuery] int? limit = null,
        CancellationToken ct = default)
    {
        if (start >= end)
            return BadRequest("Start time must be before end time.");

        if (!ListOrder.IsValid(order))
            return BadRequest("order must be 'newest' or 'oldest'.");

        var guard = CheckRawSearchWindow(q, start, end);
        if (!guard.Allowed)
            return BadRequest(guard.Message);

        try
        {
            var result = await _logs.GetLogListAsync(new LogQuery
            {
                Start = start,
                End = end,
                Service = service,
                MinSeverity = minSeverity,
                Search = q,
                Order = order.ToLowerInvariant(),
                Limit = Math.Clamp(limit ?? _capabilities.Limits.Logs, 1, _capabilities.Limits.Logs)
            }, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // GET /api/tenants/{tenantId}/logs/facets?start=&end=&service=&minSeverity=&q=&keys=&valueLimit=
    [TelemetryOperation(TelemetryOperation.Read)]
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

    // GET /api/tenants/{tenantId}/logs/by-trace/{traceId}
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("by-trace/{traceId}")]
    public async Task<ActionResult<IEnumerable<LogRecordModel>>> GetLogsByTrace(
        string traceId,
        CancellationToken ct = default)
    {
        var logs = await _logs.GetLogRecordsByTraceIdAsync(traceId, ct);
        return Ok(logs);
    }

    // GET /api/tenants/{tenantId}/logs/context?anchor=<timeUnixNano>&service=&before=&after=
    // The N log records before/after the anchor timestamp for the same service, ignoring list filters.
    [TelemetryOperation(TelemetryOperation.Read)]
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
    /// GET /api/tenants/{tenantId}/logs/export?start=&end=&service=&minSeverity=&q=&format=ndjson|csv
    /// Phase 8 (list-pages-server-side plan, decision 17): full records, same filters as
    /// <see cref="GetSummary"/>/<see cref="GetList"/>, streamed with no row cap. Bounded to
    /// <see cref="ProviderCapabilities.ExportMaxWindowDays"/> (400 beyond it) and
    /// <see cref="ExportConcurrencyGate"/>'s slot count (429 when exhausted). <c>ct</c> is bound by
    /// MVC to <c>HttpContext.RequestAborted</c>, which is what propagates a client disconnect down
    /// into the repository's own cancellation-aware read.
    /// </summary>
    [TelemetryOperation(TelemetryOperation.Export)]
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
