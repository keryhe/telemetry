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
    public ExemplarModel? Exemplar { get; set; }
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
    public ExemplarModel? Exemplar { get; set; }
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

public class MetricSeries
{
    public string Name { get; set; } = null!;
    public MetricType Type { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
    public List<MetricDataPoint> Points { get; set; } = new();
}

public class MetricDataPoint
{
    public DateTime? StartTimestamp { get; set; }
    public DateTime Timestamp { get; set; }
    public double? DoubleValue { get; set; }
    public long? IntValue { get; set; }
    public long? Count { get; set; }
    public double? Sum { get; set; }
    public int? Scale { get; set; }
    public long? ZeroCount { get; set; }
    public int? PositiveOffset { get; set; }
    public List<long>? PositiveBucketCounts { get; set; }
    public int? NegativeOffset { get; set; }
    public List<long>? NegativeBucketCounts { get; set; }
    public AggregationTemporality? AggregationTemporality { get; set; }
    public bool? IsMonotonic { get; set; }
    public int Flags { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public List<double>? Quantiles { get; set; }
    public List<double>? QuantileValues { get; set; }
    public List<long>? BucketCounts { get; set; }
    public List<double>? BucketBounds { get; set; }
    public Dictionary<string, object>? Attributes { get; set; }
    public List<ExemplarModel>? Exemplars { get; set; }
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

public class ServiceMetricSummary
{
    public string ServiceName { get; set; } = null!;
    public int MetricCount { get; set; }
    public int GaugeCount { get; set; }
    public int CounterCount { get; set; }
    public int HistogramCount { get; set; }
    public int SummaryCount { get; set; }
    public DateTime LastUpdated { get; set; }
}

public class MultiSeriesMetricData
{
    public string Name { get; set; } = "";
    public MetricType Type { get; set; }
    public List<NamedMetricSeries> Series { get; set; } = new();
}

public class NamedMetricSeries
{
    public string SeriesName { get; set; } = "";
    public long MetricId { get; set; }
    public List<MetricDataPoint> Points { get; set; } = new();
}