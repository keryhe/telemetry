using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[Route("api/metrics")]
public class MetricsController : ControllerBase
{
    private readonly IMetricReadRepository _metrics;

    public MetricsController(IMetricReadRepository metrics)
    {
        _metrics = metrics;
    }

    // GET /api/metrics?start=&end=&limit=
    [HttpGet]
    public async Task<ActionResult<List<MetricInfo>>> GetAllMetrics(
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var metrics = await _metrics.GetAllMetricsAsync(limit, start, end, ct);
        return Ok(metrics);
    }

    // GET /api/metrics/services?start=&end=
    [HttpGet("services")]
    public async Task<ActionResult<List<string>>> GetDistinctServices(
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        CancellationToken ct = default)
    {
        var services = await _metrics.GetDistinctServicesAsync(start, end, ct);
        return Ok(services);
    }

    // GET /api/metrics/by-name/{name}
    [HttpGet("by-name/{name}")]
    public async Task<ActionResult<List<MetricInfo>>> GetMetricsByName(string name, CancellationToken ct = default)
    {
        var metrics = await _metrics.GetMetricsByNameAsync(name, ct);
        return Ok(metrics);
    }

    // GET /api/metrics/labels/{name}
    [HttpGet("labels/{name}")]
    public async Task<ActionResult<Dictionary<string, List<string>>>> GetMetricLabels(
        string name,
        CancellationToken ct = default)
    {
        var labels = await _metrics.GetMetricLabelsAsync(name, ct);
        return Ok(labels);
    }

    // GET /api/metrics/series?metricName=&start=&end=&metricId=&labelFilter=key:value
    [HttpGet("series")]
    public async Task<ActionResult<MetricSeries>> GetMetricSeries(
        [FromQuery] string metricName,
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        [FromQuery] long? metricId,
        [FromQuery(Name = "labelFilter")] List<string>? labelFilter,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metricName))
            return BadRequest("metricName query parameter is required.");

        var filters = ParseLabelFilters(labelFilter);
        var series = await _metrics.GetMetricSeriesAsync(metricName, filters, start, end, metricId, ct);
        if (series == null)
            return NotFound();
        return Ok(series);
    }

    // GET /api/metrics/series-grouped?metricName=&start=&end=&metricId=&labelFilter=key:value
    [HttpGet("series-grouped")]
    public async Task<ActionResult<MultiSeriesMetricData>> GetGroupedMetricSeries(
        [FromQuery] string metricName,
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        [FromQuery] long? metricId,
        [FromQuery(Name = "labelFilter")] List<string>? labelFilter,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metricName))
            return BadRequest("metricName query parameter is required.");

        var filters = ParseLabelFilters(labelFilter);
        var data = await _metrics.GetGroupedMetricSeriesAsync(metricName, start, end, metricId, filters, ct);
        if (data == null)
            return NotFound();
        return Ok(data);
    }

    // GET /api/metrics/series-by-service/{metricName}?start=&end=
    [HttpGet("series-by-service/{metricName}")]
    public async Task<ActionResult<MultiSeriesMetricData>> GetMetricSeriesByService(
        string metricName,
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        CancellationToken ct = default)
    {
        var data = await _metrics.GetMetricSeriesByServiceAsync(metricName, start, end, ct);
        if (data == null)
            return NotFound();
        return Ok(data);
    }

    private static Dictionary<string, string>? ParseLabelFilters(List<string>? labelFilter)
    {
        if (labelFilter == null || labelFilter.Count == 0)
            return null;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in labelFilter)
        {
            var colonIdx = filter.IndexOf(':');
            if (colonIdx > 0)
                dict[filter[..colonIdx]] = filter[(colonIdx + 1)..];
        }
        return dict.Count > 0 ? dict : null;
    }
}
