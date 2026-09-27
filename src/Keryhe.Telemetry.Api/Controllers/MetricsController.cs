using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[Route("api/metrics")]
public class MetricsController : ControllerBase
{
    private readonly IMetricReadRepository _metrics;
    private readonly ProviderCapabilities _capabilities;

    public MetricsController(IMetricReadRepository metrics, ProviderCapabilities capabilities)
    {
        _metrics = metrics;
        _capabilities = capabilities;
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

    // GET /api/metrics/summary?start=&end=
    // True unique-metric-name-per-type counts over the full unbounded range, unaffected by the /api/metrics limit cap.
    [HttpGet("summary")]
    public async Task<ActionResult<MetricsSummary>> GetMetricsSummary(
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        CancellationToken ct = default)
    {
        var summary = await _metrics.GetMetricsSummaryAsync(start, end, ct);
        return Ok(summary);
    }

    // GET /api/metrics/by-name/{name}
    [HttpGet("by-name/{name}")]
    public async Task<ActionResult<List<MetricInfo>>> GetMetricsByName(string name, CancellationToken ct = default)
    {
        var metrics = await _metrics.GetMetricsByNameAsync(name, ct);
        return Ok(metrics);
    }

    // GET /api/metrics/labels/{name}?start=&end=
    [HttpGet("labels/{name}")]
    public async Task<ActionResult<MetricLabelsResult>> GetMetricLabels(
        string name,
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        CancellationToken ct = default)
    {
        var labels = await _metrics.GetMetricLabelsAsync(name, start, end, ct);
        return Ok(labels);
    }

    /// <summary>
    /// GET /api/metrics/series?metricName=&start=&end=&metricId=&labelFilter=key:value&q=&points=&top=
    /// Phase 4 (list-pages-server-side plan): replaces the former <c>series</c>/<c>series-grouped</c>
    /// pair with one database-bucketed endpoint. <c>start</c>/<c>end</c> are required — every bucket
    /// is computed against them (decision 21).
    /// </summary>
    [HttpGet("series")]
    public async Task<ActionResult<MetricSeriesResult>> GetMetricSeries(
        [FromQuery] string metricName,
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] long? metricId,
        [FromQuery(Name = "labelFilter")] List<string>? labelFilter,
        [FromQuery] string? q,
        [FromQuery] int points = 300,
        [FromQuery] int top = 8,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metricName))
            return BadRequest("metricName query parameter is required.");

        var filters = MergeLabelFilters(labelFilter, q);
        var query = new MetricSeriesQuery
        {
            MetricName = metricName,
            MetricId = metricId,
            Start = start,
            End = end,
            LabelFilters = filters,
            Points = Math.Clamp(points, 1, 1000),
            Top = Math.Max(1, top)
        };

        var series = await _metrics.GetMetricSeriesAsync(query, ct);
        if (series == null)
            return NotFound();
        return Ok(series);
    }

    /// <summary>
    /// GET /api/metrics/exemplars?metricName=&start=&end=&metricId=&labelFilter=key:value&q=&size=&cursor=&nav=
    /// Analytics tier (<see cref="ProviderCapabilities.ExemplarPaging"/>): real keyset paging —
    /// <c>cursor</c>/<c>nav</c> are honored and the response carries <c>nextCursor</c>/
    /// <c>prevCursor</c>/<c>total</c>/<c>totalIsLowerBound</c>. Standard tier: the newest 500,
    /// <c>capped</c> flagged, no cursor (decision 26). <c>end</c> is the pin the exemplar scan
    /// itself uses, not a server-echoed clock value (see the repository's own doc comment).
    /// </summary>
    [HttpGet("exemplars")]
    public async Task<ActionResult<MetricExemplarPage>> GetMetricExemplars(
        [FromQuery] string metricName,
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] long? metricId,
        [FromQuery(Name = "labelFilter")] List<string>? labelFilter,
        [FromQuery] string? q,
        [FromQuery] int size = 100,
        [FromQuery] string? cursor = null,
        [FromQuery] string nav = "first",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metricName))
            return BadRequest("metricName query parameter is required.");

        var filters = MergeLabelFilters(labelFilter, q);
        var query = new MetricExemplarQuery
        {
            MetricName = metricName,
            MetricId = metricId,
            Start = start,
            End = end,
            LabelFilters = filters,
            Size = _capabilities.ExemplarPaging ? Math.Clamp(size, 1, 1000) : 500,
            Cursor = cursor,
            Nav = nav
        };

        var page = await _metrics.GetMetricExemplarsAsync(query, ct);
        if (page == null)
            return NotFound();
        return Ok(page);
    }

    /// <summary>
    /// Merges repeated <c>labelFilter=key:value</c> parameters with the <c>q</c> grammar's
    /// <c>key:value</c> terms (decision 30). <c>q</c>'s free-text terms have no field to match on
    /// a fixed <c>metricName</c> query and are ignored; only its attribute-filter terms
    /// (non-negated — a plain label-filter dictionary can't express "not") contribute.
    /// </summary>
    private static Dictionary<string, string>? MergeLabelFilters(List<string>? labelFilter, string? q)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (labelFilter != null)
        {
            foreach (var filter in labelFilter)
            {
                var colonIdx = filter.IndexOf(':');
                if (colonIdx > 0)
                    dict[filter[..colonIdx]] = filter[(colonIdx + 1)..];
            }
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var parsed = SearchQueryParser.Parse(q);
            foreach (var term in parsed.Terms)
            {
                if (term.IsAttributeFilter && !term.Negate && term.Key != null && term.Value != null)
                    dict[term.Key] = term.Value;
            }
        }

        return dict.Count > 0 ? dict : null;
    }
}
