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
    public int Flags { get; set; } = 0;
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
    public int Flags { get; set; } = 0;
    public int DroppedAttributesCount { get; set; } = 0;
    public Dictionary<string, object>? Attributes { get; set; }
}

// =============================================================================
// TRACE QUERY RESULT CLASSES
// =============================================================================

/// <summary>A trace's extent (its earliest span start and latest span end), as the trace list returns it; see <c>ITraceReadRepository.GetTraceByIdAsync</c>.</summary>
public readonly record struct TraceTimeHint(DateTime Start, DateTime End);

public class TraceInfo
{
    public string TraceIdHex { get; set; } = null!;
    public int SpanCount { get; set; }
    public DateTime TraceStartTime { get; set; }
    public DateTime TraceEndTime { get; set; }

    /// <summary>
    /// The ANCHOR span's own duration (end - start). The anchor is the trace's earliest span in
    /// scope: the selected service's own earliest span when a service filter produced this row, the
    /// whole trace's earliest span otherwise (schema-simplification decisions 10-11). The row's
    /// <see cref="ServiceName"/>, <see cref="RootOperationName"/>, <see cref="AnchorKind"/> and
    /// <see cref="DisplaySpanIdHex"/> are the anchor's too; <see cref="HasErrors"/> is whether any span
    /// in scope has status ERROR; <see cref="SpanCount"/> counts the spans in scope. The slow filter
    /// and the summary charts use the same duration. <see cref="TraceStartTime"/>/<see cref="TraceEndTime"/>
    /// span the WHOLE trace, for the trace-detail link.
    /// </summary>
    public TimeSpan TraceDuration { get; set; }
    public string? ServiceName { get; set; }

    /// <summary>The anchor span's name -- what the operation filter matches (decision 15).</summary>
    public string? RootOperationName { get; set; }

    /// <summary>The anchor span's kind (<c>SERVER</c>, <c>CLIENT</c>, ...): the list shows every kind, the summary's request-count card counts only inbound ones (decision 12).</summary>
    public string? AnchorKind { get; set; }
    public bool HasErrors { get; set; }
    public List<string> Services { get; set; } = new();
    public Dictionary<string, object>? RootSpanAttributes { get; set; }

    /// <summary>
    /// The anchor span's id -- the trace's earliest span, or the filtered service's own
    /// earliest-started span (its entry point into this trace). Always populated; the client uses it
    /// to deep-link into the trace-detail page with that span expanded.
    /// </summary>
    public string? DisplaySpanIdHex { get; set; }
}

/// <summary>Per-operation RED metrics (Rate, Errors, Duration percentiles) for the Analytics tab.</summary>
public class OperationStats
{
    public string Operation { get; set; } = null!;
    public int Count { get; set; }
    public int ErrorCount { get; set; }

    /// <summary>Errors as a percentage of calls (0–100).</summary>
    public double ErrorRate { get; set; }

    /// <summary>Throughput: calls per second across the queried window.</summary>
    public double RatePerSecond { get; set; }

    public double AvgMs { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
}

/// <summary>
/// Per-service RED metrics for the dashboard's service health table, grouped by each trace
/// anchor's service.
/// </summary>
public class ServiceStats
{
    public string Service { get; set; } = null!;
    public int Count { get; set; }
    public int ErrorCount { get; set; }

    /// <summary>Errors as a percentage of calls (0–100).</summary>
    public double ErrorRate { get; set; }

    /// <summary>Throughput: traces per second across the queried window.</summary>
    public double RatePerSecond { get; set; }

    public double AvgMs { get; set; }
    public double P95Ms { get; set; }
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