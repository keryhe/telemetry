

namespace Keryhe.Telemetry.Core.Models;

// =============================================================================
// Models FOR LOG DATA INPUT
// =============================================================================

public class LogRecordModel
{
    public long? TimeUnixNano { get; set; }
    public long? ObservedTimeUnixNano { get; set; }
    public int? SeverityNumber { get; set; }
    public string? SeverityText { get; set; }
    public AttributeType? BodyType { get; set; } = AttributeType.STRING;
    public string? BodyValue { get; set; }
    public int DroppedAttributesCount { get; set; } = 0;
    public int Flags { get; set; } = 0;
    public string? TraceIdHex { get; set; } // Hex string representation
    public string? SpanIdHex { get; set; }  // Hex string representation
    public Dictionary<string, object> Attributes { get; set; } = new Dictionary<string, object>();
    
    // Resource information
    public ResourceModel? Resource { get; set; }
    
    // Instrumentation scope information
    public InstrumentationScopeModel? InstrumentationScope { get; set; }
}
