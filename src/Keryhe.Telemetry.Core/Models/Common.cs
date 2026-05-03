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

public enum AggregationTemporality
{
    UNSPECIFIED,
    DELTA,
    CUMULATIVE
}