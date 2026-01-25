namespace Keryhe.Telemetry.Core.Models;

// =============================================================================
// Models FOR TRACE DATA INPUT
// =============================================================================

public class TraceModel
{
    public List<SpanModel> Spans { get; set; } = new();
    public ResourceModel? Resource { get; set; }
    public InstrumentationScopeModel? InstrumentationScope { get; set; }
}

public class SpanModel
{
    public string TraceIdHex { get; set; } = null!; // 32-char hex string
    public string SpanIdHex { get; set; } = null!;  // 16-char hex string
    public string? ParentSpanIdHex { get; set; }    // 16-char hex string
    public string Name { get; set; } = null!;
    public SpanKind Kind { get; set; } = SpanKind.UNSPECIFIED;
    public long StartTimeUnixNano { get; set; }
    public long EndTimeUnixNano { get; set; }
    public int DroppedAttributesCount { get; set; } = 0;
    public int DroppedEventsCount { get; set; } = 0;
    public int DroppedLinksCount { get; set; } = 0;
    public string? TraceState { get; set; }
    public SpanStatusCode StatusCode { get; set; } = SpanStatusCode.UNSET;
    public string? StatusMessage { get; set; }
    
    // Related data
    public Dictionary<string, object>? Attributes { get; set; }
    public List<SpanEventModel> Events { get; set; } = new();
    public List<SpanLinkModel> Links { get; set; } = new();
    
    // Optional resource and scope overrides (if different from trace-level)
    public ResourceModel? Resource { get; set; }
    public InstrumentationScopeModel? InstrumentationScope { get; set; }
}

public class SpanEventModel
{
    public string Name { get; set; } = null!;
    public long TimeUnixNano { get; set; }
    public int DroppedAttributesCount { get; set; } = 0;
    public Dictionary<string, object>? Attributes { get; set; }
}

public class SpanLinkModel
{
    public string LinkedTraceIdHex { get; set; } = null!; // 32-char hex string
    public string LinkedSpanIdHex { get; set; } = null!;  // 16-char hex string
    public string? TraceState { get; set; }
    public int DroppedAttributesCount { get; set; } = 0;
    public Dictionary<string, object>? Attributes { get; set; }
}

// =============================================================================
// TRACE QUERY RESULT CLASSES
// =============================================================================

public class TraceInfo
{
    public string TraceIdHex { get; set; } = null!;
    public int SpanCount { get; set; }
    public DateTime TraceStartTime { get; set; }
    public DateTime TraceEndTime { get; set; }
    public TimeSpan TraceDuration { get; set; }
    public string? ServiceName { get; set; }
    public string? RootOperationName { get; set; }
    public bool HasErrors { get; set; }
    public List<string> Services { get; set; } = new();
}

public class ServiceDependency
{
    public string ParentService { get; set; } = null!;
    public string ChildService { get; set; } = null!;
    public SpanKind SpanKind { get; set; }
    public int CallCount { get; set; }
    public double AvgDurationMs { get; set; }
    public double MinDurationMs { get; set; }
    public double MaxDurationMs { get; set; }
    public int ErrorCount { get; set; }
    public double ErrorRate { get; set; }
}