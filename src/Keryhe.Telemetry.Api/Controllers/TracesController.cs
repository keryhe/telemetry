using System.Text.Json;
using Keryhe.Telemetry.Api.Export;
using Keryhe.Telemetry.Api.Models;
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
[Route("traces")]
public class TracesController : ControllerBase
{
    private readonly ITraceReadRepository _traces;
    private readonly ProviderCapabilities _capabilities;
    private readonly ExportConcurrencyGate _exportGate;
    private readonly IRollupReadRepository _rollups;
    private readonly RollupOptions _rollupOptions;
    private readonly TimeProvider _time;

    public TracesController(ITraceReadRepository traces, ProviderCapabilities capabilities, ExportConcurrencyGate exportGate,
        IRollupReadRepository rollups, IOptions<RollupOptions> rollupOptions, TimeProvider time)
    {
        _rollups = rollups;
        _rollupOptions = rollupOptions.Value;
        _time = time;
        _traces = traces;
        _capabilities = capabilities;
        _exportGate = exportGate;
    }

    // GET /api/tenants/{tenantId}/traces/summary?start=&end=&service=&bucketCount=
    // Cards and charts over the request rollup (inbound spans), not over traces (plans/summary-rollups.md).
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("summary")]
    public async Task<ActionResult<RequestSummaryResult>> GetSummary(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] string? service = null,
        [FromQuery] int bucketCount = 60,
        CancellationToken ct = default)
    {
        if (start >= end)
            return BadRequest("Start time must be before end time.");

        var writtenThrough = RollupSummaryBuilder.WrittenThrough(_time.GetUtcNow().UtcDateTime, _rollupOptions);
        var window = RollupSummaryBuilder.PlanWindow(start, end, writtenThrough, Math.Clamp(bucketCount, 1, 200));
        if (window.IsEmpty)
            return Ok(RollupSummaryBuilder.BuildRequestSummary(window, [], writtenThrough));

        var read = await _rollups.GetRequestRollupAsync(new RollupQuery
        {
            StartNano = window.StartNano,
            EndNano = window.EndNano,
            BucketSeconds = window.BucketSeconds,
            Service = service
        }, ct);
        return Ok(RollupSummaryBuilder.BuildRequestSummary(window, read.Rows.ToList(), writtenThrough, read.TimedOut));
    }

    // GET /api/tenants/{tenantId}/traces/page?start=&end=&asOf=&mode=&service=&operation=&minDurationMs=&maxDurationMs=&q=&size=&cursor=&nav=
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("page")]
    public async Task<ActionResult<TracePageResult>> GetPage(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] DateTime? asOf = null,
        [FromQuery] string mode = "all",
        [FromQuery] string? service = null,
        [FromQuery] string? operation = null,
        [FromQuery] double? minDurationMs = null,
        [FromQuery] double? maxDurationMs = null,
        [FromQuery] string? q = null,
        [FromQuery] int size = 100,
        [FromQuery] string? cursor = null,
        [FromQuery] string nav = "first",
        CancellationToken ct = default)
    {
        if (start >= end)
            return BadRequest("Start time must be before end time.");

        var guard = CheckRawSearchWindow(q, mode, start, end);
        if (!guard.Allowed)
            return BadRequest(guard.Message);

        try
        {
            var result = await _traces.GetTracePageAsync(new TraceQuery
            {
                Start = start,
                End = end,
                Mode = mode,
                Service = service,
                Operation = operation,
                MinDurationMs = minDurationMs,
                MaxDurationMs = maxDurationMs,
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

    // GET /api/tenants/{tenantId}/traces/samples?start=&end=&kind=errors|slowest&limit=
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("samples")]
    public async Task<ActionResult<List<TraceInfo>>> GetSamples(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] string kind = "errors",
        [FromQuery] int limit = 5,
        CancellationToken ct = default)
    {
        if (start >= end)
            return BadRequest("Start time must be before end time.");

        var result = await _traces.GetTraceSamplesAsync(new TraceSamplesQuery
        {
            Start = start,
            End = end,
            Kind = kind,
            Limit = limit
        }, ct);
        if (result.TimedOut) Response.Headers[TimedOutHeader] = "true";
        return Ok(result.Items);
    }

    /// <summary>
    /// Set on <c>samples</c> when the anchor scan ran out of time: the (empty) array means "unknown", not "none". A header
    /// rather than a body field so the response stays the bare array existing consumers read.
    /// </summary>
    public const string TimedOutHeader = "X-Telemetry-Timed-Out";

    /// <summary>
    /// Decision 39's standard-tier raw-search-window guard. A trace-id-shaped <c>q</c> and
    /// <c>mode=errors</c> (served by <c>idx_spans_error</c>) are exempt; <c>mode=slow</c>'s
    /// duration filter and any other non-empty <c>q</c> count as a raw search filter.
    /// </summary>
    private RawSearchWindowGuard.Result CheckRawSearchWindow(string? q, string mode, DateTime start, DateTime end)
    {
        var parsed = SearchQueryParser.Parse(q);
        var hasRawSearchFilter = parsed.Terms.Count > 0 || mode == "slow";
        var isExempt = parsed.IsTraceIdSearch || mode == "errors";
        return RawSearchWindowGuard.Check(_capabilities, hasRawSearchFilter, isExempt, end - start);
    }

    // GET /api/tenants/{tenantId}/traces/{traceId}/spans?start=&end=
    /// <summary>
    /// The trace's spans, with each distinct resource and instrumentation scope listed once (<see cref="TraceDetailResponse"/>).
    /// <c>start</c> and <c>end</c> are optional and used together: the trace's extent as the list returns it
    /// (<c>traceStartTime</c>/<c>traceEndTime</c>), which lets a provider that cannot seek a trace id (ClickHouse) read only
    /// that range. With either one missing the read is unbounded and the trace whole.
    /// </summary>
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("{traceId}/spans")]
    public async Task<ActionResult<TraceDetailResponse>> GetSpans(
        string traceId, [FromQuery] DateTime? start = null, [FromQuery] DateTime? end = null, CancellationToken ct = default)
    {
        TraceTimeHint? hint = start.HasValue && end.HasValue && start.Value <= end.Value ? new TraceTimeHint(start.Value, end.Value) : null;
        var spans = await _traces.GetTraceByIdAsync(traceId, hint, ct);
        if (spans.Count == 0)
            return NotFound();
        return Ok(TraceDetailResponse.From(spans));
    }

    // GET /api/tenants/{tenantId}/traces/dependencies?start=&end=
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("dependencies")]
    public async Task<ActionResult<List<ServiceDependency>>> GetDependencies(
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        CancellationToken ct = default)
    {
        var deps = await _traces.GetServiceDependenciesAsync(start, end, ct);
        return Ok(deps);
    }

    // GET /api/tenants/{tenantId}/traces/operations?service=&start=&end=
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("operations")]
    public async Task<ActionResult<Dictionary<string, int>>> GetOperationCounts(
        [FromQuery] string service,
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest("service query parameter is required.");
        var counts = await _traces.GetOperationCountsAsync(service, start, end, ct);
        return Ok(counts);
    }

    // GET /api/tenants/{tenantId}/traces/operations/stats?service=&start=&end=
    // Per-operation RED metrics (rate, error%, p50/p95/p99, avg) for the Analytics tab.
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("operations/stats")]
    public async Task<ActionResult<List<OperationStats>>> GetOperationStats(
        [FromQuery] string service,
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest("service query parameter is required.");
        var stats = await _traces.GetOperationStatsAsync(service, start, end, ct);
        return Ok(stats);
    }

    // GET /api/tenants/{tenantId}/traces/latencies?service=&start=&end=
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("latencies")]
    public async Task<ActionResult<Dictionary<string, double>>> GetAverageLatencies(
        [FromQuery] string service,
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest("service query parameter is required.");
        var latencies = await _traces.GetAverageLatenciesAsync(service, start, end, ct);
        return Ok(latencies);
    }

    /// <summary>
    /// GET /api/tenants/{tenantId}/traces/export?start=&end=&mode=&service=&operation=&minDurationMs=&maxDurationMs=&q=&format=ndjson|csv
    /// Phase 8 (list-pages-server-side plan, decision 17): one trace-summary row per trace, same
    /// filters as <see cref="GetSummary"/>/<see cref="GetPage"/>, streamed with no row cap.
    /// Span-level export is out of scope (Target API/plan text). Same window/concurrency limits as
    /// the logs export — see <see cref="LogsController.GetExport"/>'s doc comment.
    /// </summary>
    [TelemetryOperation(TelemetryOperation.Export)]
    [HttpGet("export")]
    public async Task<IActionResult> GetExport(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] string mode = "all",
        [FromQuery] string? service = null,
        [FromQuery] string? operation = null,
        [FromQuery] double? minDurationMs = null,
        [FromQuery] double? maxDurationMs = null,
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

        var query = new TraceExportQuery
        {
            Start = start,
            End = end,
            Mode = mode,
            Service = service,
            Operation = operation,
            MinDurationMs = minDurationMs,
            MaxDurationMs = maxDurationMs,
            Search = q
        };

        Response.ContentType = exportFormat.ContentType();
        Response.Headers.ContentDisposition = $"attachment; filename=\"traces-export.{exportFormat.FileExtension()}\"";

        var rowCount = 0;
        if (exportFormat == ExportFormat.Csv)
        {
            await using var csv = new CsvRowWriter(Response.Body);
            await csv.WriteHeaderAsync(["traceId", "spanCount", "startTime", "endTime", "durationMs", "serviceName", "rootOperationName", "hasErrors"]);
            await foreach (var trace in _traces.ExportTracesAsync(query, ct))
            {
                await csv.WriteRowAsync(TraceCsvCells(trace));
                if (++rowCount % 500 == 0)
                    await csv.FlushAsync();
            }
        }
        else
        {
            await foreach (var trace in _traces.ExportTracesAsync(query, ct))
            {
                await NdjsonRowWriter.WriteLineAsync(Response.Body, trace, ct);
                if (++rowCount % 500 == 0)
                    await Response.Body.FlushAsync(ct);
            }
        }

        await Response.Body.FlushAsync(ct);
        return new EmptyResult();
    }

    private static IEnumerable<string?> TraceCsvCells(TraceInfo trace)
    {
        yield return trace.TraceIdHex;
        yield return trace.SpanCount.ToString();
        yield return trace.TraceStartTime.ToString("O");
        yield return trace.TraceEndTime.ToString("O");
        yield return trace.TraceDuration.TotalMilliseconds.ToString("F3");
        yield return trace.ServiceName;
        yield return trace.RootOperationName;
        yield return trace.HasErrors.ToString();
    }
}
