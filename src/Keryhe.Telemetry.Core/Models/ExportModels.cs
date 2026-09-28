namespace Keryhe.Telemetry.Core.Models;

// =============================================================================
// Phase 8 (list-pages-server-side plan): streaming export request/row shapes for
// GET /api/logs/export, /api/traces/export and /api/metrics/export.
// =============================================================================

/// <summary>Logs export request — the exact same filters as <see cref="LogQuery"/>/<see cref="LogSummaryQuery"/>, minus paging/AsOf (decision 17: full records, no row cap, streamed).</summary>
public sealed class LogExportQuery
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string? Service { get; init; }
    public int? MinSeverity { get; init; }
    public string? Search { get; init; }
}

/// <summary>Traces export request — the exact same filters as <see cref="TraceQuery"/>/<see cref="TraceSummaryQuery"/>. One <see cref="TraceInfo"/> row per trace (decision 17); span-level export is out of scope.</summary>
public sealed class TraceExportQuery
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string Mode { get; init; } = "all";
    public string? Service { get; init; }
    public string? Operation { get; init; }
    public double? MinDurationMs { get; init; }
    public double? MaxDurationMs { get; init; }
    public string? Search { get; init; }
}

/// <summary>
/// Metrics export request (decision 29): the same shape as <see cref="MetricSeriesQuery"/>, minus
/// <c>Top</c> — export always includes every display series, never a top-N/"other" split.
/// </summary>
public sealed class MetricExportQuery
{
    public string MetricName { get; set; } = "";
    public long? MetricId { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public Dictionary<string, string>? LabelFilters { get; set; }
    public int Points { get; set; } = 300;
}

/// <summary>
/// One exported row: a single <c>(display series, bucket)</c> pair (decision 29). Gauge populates
/// <see cref="Value"/>/<see cref="Min"/>/<see cref="Max"/>; Sum populates <see cref="Value"/> only;
/// Histogram/exponential histogram populate <see cref="Count"/>/<see cref="Sum"/>/
/// <see cref="BucketCounts"/>/<see cref="BucketBounds"/> (merged, per decision 22); Summary
/// populates <see cref="Quantiles"/>/<see cref="QuantileValues"/> — the same per-type shape as
/// <see cref="MetricBucketPoint"/>, which this wraps with the series/metric identity every row of a
/// flat export needs restated (unlike the nested <see cref="MetricSeriesResult"/> shape the
/// <c>/series</c> endpoint returns).
/// </summary>
public sealed class MetricExportRow
{
    public string MetricName { get; set; } = "";
    public string SeriesName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public Dictionary<string, string> Labels { get; set; } = new();
    public DateTime BucketStart { get; set; }
    public double? Value { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public long? Count { get; set; }
    public double? Sum { get; set; }
    public List<long>? BucketCounts { get; set; }
    public List<double>? BucketBounds { get; set; }
    public List<double>? Quantiles { get; set; }
    public List<double>? QuantileValues { get; set; }
}
