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

public class TraceInfo
{
    public string TraceIdHex { get; set; } = null!;
    public int SpanCount { get; set; }
    public DateTime TraceStartTime { get; set; }
    public DateTime TraceEndTime { get; set; }

    /// <summary>
    /// Whole-trace duration, unless a service filter produced this row — then it's that
    /// service's own duration (the span of time between its earliest and latest span in this
    /// trace), not the whole trace's. Same conditional applies to <see cref="ServiceName"/>,
    /// <see cref="RootOperationName"/>, <see cref="HasErrors"/>, and <see cref="RootSpanAttributes"/>:
    /// each reflects the filtered service's own spans when a service filter matched this trace,
    /// and the trace's true root otherwise.
    /// </summary>
    public TimeSpan TraceDuration { get; set; }
    public string? ServiceName { get; set; }
    public string? RootOperationName { get; set; }
    public bool HasErrors { get; set; }
    public List<string> Services { get; set; } = new();
    public Dictionary<string, object>? RootSpanAttributes { get; set; }

    /// <summary>
    /// The span id backing this row's displayed info — the trace's true root span when no
    /// service filter is active, or the filtered service's own earliest-started span
    /// (its entry point into this trace) otherwise. Always populated; the client uses it to
    /// deep-link into the trace-detail page with that span expanded.
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
/// Per-service RED metrics for the dashboard's service health table, grouped by each trace's
/// root-span service — see <see cref="TraceOverview"/> for how this is produced.
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