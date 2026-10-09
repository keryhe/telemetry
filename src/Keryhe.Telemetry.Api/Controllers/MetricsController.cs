using Keryhe.Telemetry.Api.Export;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Http;
using Keryhe.Telemetry.Api.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

[ApiController]
[TenantScoped]
[Route("metrics")]
public class MetricsController : ControllerBase
{
    private readonly IMetricReadRepository _metrics;
    private readonly ProviderCapabilities _capabilities;
    private readonly ExportConcurrencyGate _exportGate;

    public MetricsController(IMetricReadRepository metrics, ProviderCapabilities capabilities, ExportConcurrencyGate exportGate)
    {
        _metrics = metrics;
        _capabilities = capabilities;
        _exportGate = exportGate;
    }

    /// <summary>
    /// GET /api/tenants/{tenantId}/metrics/catalog?start=&end=&q=&service=&type=&groupBy=name|instance&limit=
    /// Phase 5 (list-pages-server-side plan): replaces the former unbounded-with-a-cap
    /// <c>GET /api/metrics</c> (removed — its only caller, the metrics list page, now calls this).
    /// <c>start</c>/<c>end</c> are required — every "seen in range" check runs against them
    /// (decision 27).
    /// </summary>
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("catalog")]
    public async Task<ActionResult<MetricCatalogPage>> GetMetricCatalog(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] string? q,
        [FromQuery] string? service,
        [FromQuery] MetricType? type,
        [FromQuery] string groupBy = "instance",
        [FromQuery] int? limit = null,
        CancellationToken ct = default)
    {
        if (start >= end)
            return BadRequest("start must be before end.");

        var query = new MetricCatalogQuery
        {
            Start = start,
            End = end,
            Q = q,
            Service = service,
            Type = type,
            GroupBy = groupBy,
            Limit = Math.Clamp(limit ?? _capabilities.Limits.MetricCatalog, 1, _capabilities.Limits.MetricCatalog)
        };

        var page = await _metrics.GetMetricCatalogPageAsync(query, ct);
        return Ok(page);
    }

    // GET /api/tenants/{tenantId}/metrics/summary?start=&end=
    // True unique-metric-name-per-type counts over the full unbounded range, unaffected by the /api/metrics limit cap.
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("summary")]
    public async Task<ActionResult<MetricsSummary>> GetMetricsSummary(
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        CancellationToken ct = default)
    {
        var summary = await _metrics.GetMetricsSummaryAsync(start, end, ct);
        return Ok(summary);
    }

    // GET /api/tenants/{tenantId}/metrics/by-name/{name}
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("by-name/{name}")]
    public async Task<ActionResult<List<MetricInfo>>> GetMetricsByName(string name, CancellationToken ct = default)
    {
        var metrics = await _metrics.GetMetricsByNameAsync(name, ct);
        return Ok(metrics);
    }

    // GET /api/tenants/{tenantId}/metrics/labels/{name}?start=&end=
    [TelemetryOperation(TelemetryOperation.Read)]
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
    /// GET /api/tenants/{tenantId}/metrics/series?metricName=&start=&end=&metricId=&labelFilter=key:value&q=&points=&top=
    /// Phase 4 (list-pages-server-side plan): replaces the former <c>series</c>/<c>series-grouped</c>
    /// pair with one database-bucketed endpoint. <c>start</c>/<c>end</c> are required — every bucket
    /// is computed against them (decision 21).
    /// </summary>
    [TelemetryOperation(TelemetryOperation.Read)]
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
    /// GET /api/tenants/{tenantId}/metrics/exemplars?metricName=&start=&end=&metricId=&labelFilter=key:value&q=&limit=
    /// The newest exemplars first, at most <c>limit</c> (clamped to <c>Telemetry:Query:Limits:Exemplars</c>); <c>truncated</c> says more exist.
    /// </summary>
    [TelemetryOperation(TelemetryOperation.Read)]
    [HttpGet("exemplars")]
    public async Task<ActionResult<MetricExemplarPage>> GetMetricExemplars(
        [FromQuery] string metricName,
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] long? metricId,
        [FromQuery(Name = "labelFilter")] List<string>? labelFilter,
        [FromQuery] string? q,
        [FromQuery] int? limit = null,
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
            Limit = Math.Clamp(limit ?? _capabilities.Limits.Exemplars, 1, _capabilities.Limits.Exemplars)
        };

        var page = await _metrics.GetMetricExemplarsAsync(query, ct);
        if (page == null)
            return NotFound();
        return Ok(page);
    }

    /// <summary>
    /// GET /api/tenants/{tenantId}/metrics/export?metricName=&start=&end=&metricId=&labelFilter=key:value&q=&points=&format=ndjson|csv
    /// Phase 8 (list-pages-server-side plan, decision 29): one row per <c>(display series,
    /// bucket)</c>, reusing <c>/series</c>'s bucketing (<c>points</c>/<c>groupBy</c> behave the
    /// same) but with every series included — no top-N/"other" split, unlike <see cref="GetMetricSeries"/>.
    /// Same window/concurrency limits as the logs/traces exports — see
    /// <see cref="LogsController.GetExport"/>'s doc comment.
    /// </summary>
    [TelemetryOperation(TelemetryOperation.Export)]
    [HttpGet("export")]
    public async Task<IActionResult> GetExport(
        [FromQuery] string metricName,
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] long? metricId,
        [FromQuery(Name = "labelFilter")] List<string>? labelFilter,
        [FromQuery] string? q,
        [FromQuery] int points = 300,
        [FromQuery] string format = "ndjson",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metricName))
            return BadRequest("metricName query parameter is required.");
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

        var filters = MergeLabelFilters(labelFilter, q);
        var query = new MetricExportQuery
        {
            MetricName = metricName,
            MetricId = metricId,
            Start = start,
            End = end,
            LabelFilters = filters,
            Points = Math.Clamp(points, 1, 1000)
        };

        Response.ContentType = exportFormat.ContentType();
        Response.Headers.ContentDisposition = $"attachment; filename=\"{SanitizeFileNamePart(metricName)}-export.{exportFormat.FileExtension()}\"";

        var rowCount = 0;
        if (exportFormat == ExportFormat.Csv)
        {
            await using var csv = new CsvRowWriter(Response.Body);
            await csv.WriteHeaderAsync(["metricName", "seriesName", "serviceName", "labels", "bucketStart", "value", "min", "max", "count", "sum", "bucketCounts", "bucketBounds", "rate"]);
            await foreach (var row in _metrics.ExportMetricSeriesAsync(query, ct))
            {
                await csv.WriteRowAsync(MetricCsvCells(row));
                if (++rowCount % 500 == 0)
                    await csv.FlushAsync();
            }
        }
        else
        {
            await foreach (var row in _metrics.ExportMetricSeriesAsync(query, ct))
            {
                await NdjsonRowWriter.WriteLineAsync(Response.Body, row, ct);
                if (++rowCount % 500 == 0)
                    await Response.Body.FlushAsync(ct);
            }
        }

        await Response.Body.FlushAsync(ct);
        return new EmptyResult();
    }

    private static string SanitizeFileNamePart(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_').ToArray();
        return new string(chars);
    }

    private static IEnumerable<string?> MetricCsvCells(MetricExportRow row)
    {
        yield return row.MetricName;
        yield return row.SeriesName;
        yield return row.ServiceName;
        yield return System.Text.Json.JsonSerializer.Serialize(row.Labels);
        yield return row.BucketStart.ToString("O");
        yield return row.Value?.ToString("R");
        yield return row.Min?.ToString("R");
        yield return row.Max?.ToString("R");
        yield return row.Count?.ToString();
        yield return row.Sum?.ToString("R");
        yield return row.BucketCounts == null ? null : System.Text.Json.JsonSerializer.Serialize(row.BucketCounts);
        yield return row.BucketBounds == null ? null : System.Text.Json.JsonSerializer.Serialize(row.BucketBounds);
        yield return row.Rate?.ToString("R");
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
