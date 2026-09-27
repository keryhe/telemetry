namespace Keryhe.Telemetry.Core.Models;

// =============================================================================
// Models FOR METRICS DATA INPUT
// =============================================================================

public class MetricModel
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? Unit { get; set; }
    public MetricType Type { get; set; }
    
    // Data points for different metric types
    public List<GaugeDataPointModel>? GaugeDataPoints { get; set; }
    public List<SumDataPointModel>? SumDataPoints { get; set; }
    public List<HistogramDataPointModel>? HistogramDataPoints { get; set; }
    public List<ExponentialHistogramDataPointModel>? ExponentialHistogramDataPoints { get; set; }
    public List<SummaryDataPointModel>? SummaryDataPoints { get; set; }
    
    // Resource and scope information
    public ResourceModel? Resource { get; set; }
    public InstrumentationScopeModel? InstrumentationScope { get; set; }
}

public class GaugeDataPointModel
{
    public long? StartTimeUnixNano { get; set; }
    public long TimeUnixNano { get; set; }
    public double? ValueDouble { get; set; }
    public long? ValueInt { get; set; }
    public int Flags { get; set; } = 0;
    public Dictionary<string, object>? Attributes { get; set; }
    public List<ExemplarModel>? Exemplars { get; set; }
}

public class SumDataPointModel
{
    public long? StartTimeUnixNano { get; set; }
    public long TimeUnixNano { get; set; }
    public double? ValueDouble { get; set; }
    public long? ValueInt { get; set; }
    public AggregationTemporality AggregationTemporality { get; set; } = AggregationTemporality.UNSPECIFIED;
    public bool IsMonotonic { get; set; } = false;
    public int Flags { get; set; } = 0;
    public Dictionary<string, object>? Attributes { get; set; }
    public List<ExemplarModel>? Exemplars { get; set; }
}

public class HistogramDataPointModel
{
    public long? StartTimeUnixNano { get; set; }
    public long TimeUnixNano { get; set; }
    public long Count { get; set; }
    public double? Sum { get; set; }
    public long[]? BucketCounts { get; set; }
    public double[]? ExplicitBounds { get; set; }
    public AggregationTemporality AggregationTemporality { get; set; } = AggregationTemporality.UNSPECIFIED;
    public int Flags { get; set; } = 0;
    public double? Min { get; set; }
    public double? Max { get; set; }
    public Dictionary<string, object>? Attributes { get; set; }
    public List<ExemplarModel>? Exemplars { get; set; }
}

public class ExponentialHistogramDataPointModel
{
    public long? StartTimeUnixNano { get; set; }
    public long TimeUnixNano { get; set; }
    public long Count { get; set; }
    public double? Sum { get; set; }
    public int Scale { get; set; }
    public long ZeroCount { get; set; }
    public int? PositiveOffset { get; set; }
    public long[]? PositiveBucketCounts { get; set; }
    public int? NegativeOffset { get; set; }
    public long[]? NegativeBucketCounts { get; set; }
    public AggregationTemporality AggregationTemporality { get; set; } = AggregationTemporality.UNSPECIFIED;
    public int Flags { get; set; } = 0;
    public double? Min { get; set; }
    public double? Max { get; set; }
    public Dictionary<string, object>? Attributes { get; set; }
    public List<ExemplarModel>? Exemplars { get; set; }
}

public class SummaryDataPointModel
{
    public long? StartTimeUnixNano { get; set; }
    public long TimeUnixNano { get; set; }
    public long Count { get; set; }
    public double Sum { get; set; }
    public List<QuantileValueModel>? QuantileValues { get; set; }
    public int Flags { get; set; } = 0;
    public Dictionary<string, object>? Attributes { get; set; }
}

public class QuantileValueModel
{
    public double Quantile { get; set; }
    public double Value { get; set; }
}

public class ExemplarModel
{
    public Dictionary<string, object>? FilteredAttributes { get; set; }
    public long TimeUnixNano { get; set; }
    public double? ValueDouble { get; set; }
    public long? ValueInt { get; set; }
    public string? SpanIdHex { get; set; }
    public string? TraceIdHex { get; set; }
}

// =============================================================================
// METRICS QUERY RESULT CLASSES
// =============================================================================

public class MetricInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? Unit { get; set; }
    public MetricType Type { get; set; }
    public string? ServiceName { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public int DataPointCount { get; set; }
}

public class UniqueMetricSummary
{
    public string Name { get; set; } = "";
    public MetricType Type { get; set; }
    public string? Unit { get; set; }
    public string? Description { get; set; }
    public int InstanceCount { get; set; }
    public List<string> Services { get; set; } = new();
    public DateTime LastSeen { get; set; }
}

/// <summary>
/// One exemplar plus the identity of the data point and series it was sampled from. Served by the
/// dedicated exemplar endpoint; the series endpoints deliberately no longer carry exemplars, which
/// were ~12 MB of an 18 MB one-hour histogram response.
/// </summary>
public class MetricExemplar
{
    public ExemplarModel Exemplar { get; set; } = null!;
    public string SeriesName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public Dictionary<string, string> Labels { get; set; } = new();
    public DateTime PointTimestamp { get; set; }
    /// <summary>Owning point's observation count — distributions only; null for gauge/sum.</summary>
    public long? PointCount { get; set; }
    /// <summary>Owning point's value — gauge/sum only; null for distributions.</summary>
    public double? PointDoubleValue { get; set; }
    public long? PointIntValue { get; set; }
}

// MetricExemplarPage moved to MetricSeriesModels.cs (Phase 4: gained cursor/capped fields).

/// <summary>
/// Result of <see cref="IMetricReadRepository.GetMetricLabelsAsync"/> (list-pages-server-side
/// plan, Phase 1, decision 25): the label picker's distinct-attribute-set scan is time-bounded and
/// capped at 1,000 rows, so <see cref="Partial"/> tells the client when rare labels might be
/// missing because the cap was hit before every distinct set was seen.
/// </summary>
public sealed class MetricLabelsResult
{
    public Dictionary<string, List<string>> Labels { get; set; } = new();
    public bool Partial { get; set; }
}