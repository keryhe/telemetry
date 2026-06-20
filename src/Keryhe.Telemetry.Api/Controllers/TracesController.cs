using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[Route("api/traces")]
public class TracesController : ControllerBase
{
    private readonly ITraceReadRepository _traces;

    public TracesController(ITraceReadRepository traces)
    {
        _traces = traces;
    }

    // GET /api/traces?start=&end=&limit=&mode=all|errors|slow&service=&minDurationMs=
    [HttpGet]
    public async Task<ActionResult<List<TraceInfo>>> GetTraces(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] int limit = 100,
        [FromQuery] string mode = "all",
        [FromQuery] string? service = null,
        [FromQuery] double? minDurationMs = null,
        CancellationToken ct = default)
    {
        List<TraceInfo> result = mode switch
        {
            "errors" => await _traces.GetErrorTracesAsync(start, end, limit, ct),
            "slow"   => await _traces.GetSlowTracesAsync(
                            TimeSpan.FromMilliseconds(minDurationMs ?? 500), start, end, limit, ct),
            _        => string.IsNullOrEmpty(service)
                            ? await _traces.GetTracesByTimeRangeAsync(start, end, limit, ct)
                            : await _traces.GetTracesByServiceAsync(service, start, end, limit, ct)
        };
        return Ok(result);
    }

    // GET /api/traces/{traceId}/spans
    [HttpGet("{traceId}/spans")]
    public async Task<ActionResult<List<SpanModel>>> GetSpans(string traceId, CancellationToken ct = default)
    {
        var spans = await _traces.GetTraceByIdAsync(traceId, ct);
        if (spans.Count == 0)
            return NotFound();
        return Ok(spans);
    }

    // GET /api/traces/services?start=&end=
    [HttpGet("services")]
    public async Task<ActionResult<List<string>>> GetDistinctServices(
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        CancellationToken ct = default)
    {
        var traces = await _traces.GetTracesByTimeRangeAsync(
            start ?? DateTime.UtcNow.AddDays(-7),
            end   ?? DateTime.UtcNow,
            limit: 1000, ct);

        var services = traces
            .Where(t => !string.IsNullOrEmpty(t.ServiceName))
            .Select(t => t.ServiceName!)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        return Ok(services);
    }

    // GET /api/traces/dependencies?start=&end=
    [HttpGet("dependencies")]
    public async Task<ActionResult<List<ServiceDependency>>> GetDependencies(
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        CancellationToken ct = default)
    {
        var deps = await _traces.GetServiceDependenciesAsync(start, end, ct);
        return Ok(deps);
    }

    // GET /api/traces/operations?service=&start=&end=
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

    // GET /api/traces/latencies?service=&start=&end=
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
}
