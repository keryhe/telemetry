namespace Keryhe.Telemetry.Core.Models;

// =============================================================================
// Phase 4 (list-pages-server-side plan): database-side metric series aggregation.
// Replaces the raw MetricSeries/MultiSeriesMetricData shapes for the metric-detail chart path.
// MetricSeries/MetricDataPoint/MultiSeriesMetricData/NamedMetricSeries in Metrics.cs stay in
// place only as the row DTOs the aggregation reads internally — they are no longer returned to
// callers of IMetricReadRepository.
// =============================================================================

/// <summary>Request shape for <see cref="IMetricReadRepository.GetMetricSeriesAsync(MetricSeriesQuery, CancellationToken)"/>.</summary>
public sealed class MetricSeriesQuery
{
    public string MetricName { get; set; } = "";
    public long? MetricId { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>Merged from repeated <c>labelFilter=key:value</c> and the <c>q</c> grammar's <c>key:value</c> terms (decision 30).</summary>
    public Dictionary<string, string>? LabelFilters { get; set; }
    /// <summary>Target point count per stream (decision 21); default 300, caller-clamped to [1, 1000].</summary>
    public int Points { get; set; } = 300;
    /// <summary>Display series kept before folding the remainder into "other" (decision 23); default 8.</summary>
    public int Top { get; set; } = 8;
}

public sealed class MetricSeriesResult
{
    public string Name { get; set; } = "";
    public MetricType Type { get; set; }
    public string? Unit { get; set; }
    public long BucketWidthMs { get; set; }
    /// <summary>True when the full-resolution query timed out and a quarter-resolution retry was used, or when that retry also timed out (decision 31) — <see cref="Series"/>/<see cref="Other"/> are empty in the latter case.</summary>
    public bool TimedOut { get; set; }
    public List<DisplayMetricSeries> Series { get; set; } = new();
    public OtherMetricSeries? Other { get; set; }
}

public sealed class DisplayMetricSeries
{
    public string SeriesName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public Dictionary<string, string> Labels { get; set; } = new();
    public List<MetricBucketPoint> Points { get; set; } = new();
    /// <summary>
    /// Histogram/exponential-histogram only (decision 42): count of streams folded into this
    /// display series whose <c>explicit_bounds</c> didn't match the layout that was actually
    /// charted. Null for every other type, and null (not zero) when nothing was excluded, so the
    /// client only renders the "N streams not shown" note when this is a positive number.
    /// </summary>
    public int? ExcludedStreams { get; set; }
}

public sealed class OtherMetricSeries
{
    /// <summary>Number of display series folded into "other".</summary>
    public int SeriesCount { get; set; }
    public List<MetricBucketPoint> Points { get; set; } = new();
    public int? ExcludedStreams { get; set; }
}

/// <summary>
/// One pre-aggregated bucket for a display series (decisions 21-22). Which fields are populated
/// depends on the metric type: <see cref="Value"/>/<see cref="Min"/>/<see cref="Max"/> for
/// gauge/sum; <see cref="Count"/>/<see cref="Sum"/>/<see cref="BucketCounts"/>/
/// <see cref="BucketBounds"/>/<see cref="Min"/>/<see cref="Max"/> for histogram and exponential
/// histogram (the client derives percentiles from the merged buckets, same
/// <c>histogramQuantile</c> helper as today); <see cref="Quantiles"/>/<see cref="QuantileValues"/>
/// for summary.
/// </summary>
public sealed class MetricBucketPoint
{
    /// <summary>Bucket start time.</summary>
    public DateTime Timestamp { get; set; }
    /// <summary>Gauge: bucket average. Sum: bucket delta (summed across streams for the display series).</summary>
    public double? Value { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public long? Count { get; set; }
    public double? Sum { get; set; }
    public List<long>? BucketCounts { get; set; }
    public List<double>? BucketBounds { get; set; }
    public List<double>? Quantiles { get; set; }
    public List<double>? QuantileValues { get; set; }
    /// <summary>Summary only: true when more than one stream contributed to this bucket, so the quantiles shown are an average-of-quantiles approximation, not a true merged quantile (decision 22).</summary>
    public bool IsApproximate { get; set; }
}

/// <summary>Request shape for <see cref="IMetricReadRepository.GetMetricExemplarsAsync(MetricExemplarQuery, CancellationToken)"/>.</summary>
public sealed class MetricExemplarQuery
{
    public string MetricName { get; set; } = "";
    public long? MetricId { get; set; }
    public DateTime Start { get; set; }
    /// <summary>The window's own end — exemplars pin on this directly, never a server-echoed clock value (decision 26).</summary>
    public DateTime End { get; set; }
    public Dictionary<string, string>? LabelFilters { get; set; }
    public int Size { get; set; } = 100;
    /// <summary>Opaque, unparsed keyset cursor (analytics tier only).</summary>
    public string? Cursor { get; set; }
    /// <summary>first | next | prev | last (analytics tier only).</summary>
    public string Nav { get; set; } = "first";
}

public sealed class MetricExemplarPage
{
    public string Name { get; set; } = "";
    public MetricType Type { get; set; }
    public List<MetricExemplar> Exemplars { get; set; } = new();

    // Analytics tier (decision 26): real keyset paging.
    public string? NextCursor { get; set; }
    public string? PrevCursor { get; set; }
    public long? Total { get; set; }
    public bool TotalIsLowerBound { get; set; }

    // Standard tier (decision 26): newest-500, no cursor.
    /// <summary>True when the standard-tier scan hit its 500-row cap: more exemplars exist beyond those returned.</summary>
    public bool Capped { get; set; }
}
