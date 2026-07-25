namespace Keryhe.Telemetry.Core.Models;

public class ResourceModel
{
    public const long DefaultTenantId = 1;

    public long TenantId { get; set; } = DefaultTenantId;
    public string? SchemaUrl { get; set; }
    public Dictionary<string, object> Attributes { get; set; } = new  Dictionary<string, object>(); 
}

public class InstrumentationScopeModel
{
    public string Name { get; set; } = null!;
    public string? Version { get; set; }
    public string? SchemaUrl { get; set; }
    public Dictionary<string, object> Attributes { get; set; } = new Dictionary<string, object>();
}

// =============================================================================
// ENUMS
// =============================================================================

public enum AttributeType
{
    STRING,
    BOOL,
    INT,
    DOUBLE,
    BYTES,
    ARRAY,
    KVLIST
}

public enum SpanKind
{
    UNSPECIFIED,
    INTERNAL,
    SERVER,
    CLIENT,
    PRODUCER,
    CONSUMER
}

public enum SpanStatusCode
{
    UNSET,
    OK,
    ERROR
}

public enum MetricType
{
    GAUGE,
    SUM,
    HISTOGRAM,
    EXPONENTIAL_HISTOGRAM,
    SUMMARY
}

/// <summary>Distinct metric-name count for one <see cref="MetricType"/>, part of <see cref="MetricsSummary"/>.</summary>
public sealed class MetricTypeCount
{
    public MetricType Type { get; init; }
    public int Count { get; init; }
}

/// <summary>
/// True (unbounded) count of distinct metric names with data in a time range, broken down by
/// type — for the Metrics page stat cards, which must not be capped by the <c>/api/metrics</c>
/// row-limit used to populate the metric table.
/// </summary>
public sealed class MetricsSummary
{
    public int UniqueMetricCount { get; init; }
    public List<MetricTypeCount> CountsByType { get; init; } = new();
}

public enum AggregationTemporality
{
    UNSPECIFIED,
    DELTA,
    CUMULATIVE
}