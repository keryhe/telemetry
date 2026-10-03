namespace Keryhe.Telemetry.Core.Models;

/// <summary>
/// Server-side filter for the logs list page's <c>summary</c>/<c>page</c>/<c>facets</c> endpoints
/// (list-pages-server-side plan, Phase 2). Replaces the offset-paged <c>LogQuery</c>: <c>Search</c>
/// carries the raw <c>q</c> text, parsed server-side by <see cref="Data.Read.SearchQueryParser"/>,
/// and paging is keyset (<see cref="Cursor"/>/<see cref="Nav"/>) pinned on <see cref="AsOf"/>
/// (decision 3) rather than offset-based.
/// </summary>
public sealed class LogQuery
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }

    /// <summary>Exact <c>service.name</c> match (resource attribute), when set.</summary>
    public string? Service { get; init; }

    /// <summary>Minimum OTLP severity number (inclusive), when set.</summary>
    public int? MinSeverity { get; init; }

    /// <summary>Raw search text (decision 10): free text, <c>key:value</c>/<c>key=value</c>, negation — parsed server-side.</summary>
    public string? Search { get; init; }

    /// <summary>Page size, clamped 1-500 by the repository.</summary>
    public int Size { get; init; } = 100;

    /// <summary>Opaque keyset cursor from a previous page, or null for the first page.</summary>
    public string? Cursor { get; init; }

    /// <summary><c>first</c> | <c>next</c> | <c>prev</c> | <c>last</c> (decision 1).</summary>
    public string Nav { get; init; } = "first";

    /// <summary>
    /// The ingestion-time pin (decision 3): rows with <c>created_at &gt; AsOf</c> are excluded from
    /// every page. Null on the first request of a query, at which point the repository captures it
    /// from the database clock and returns it for the client to echo on later requests.
    /// </summary>
    public DateTime? AsOf { get; init; }
}

/// <summary>Filter for <c>GET /api/logs/summary</c> (list-pages-server-side plan, Phase 2, Target API).</summary>
public sealed class LogSummaryQuery
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string? Service { get; init; }
    public int? MinSeverity { get; init; }
    public string? Search { get; init; }
    public DateTime? AsOf { get; init; }

    /// <summary>Target bucket count for the chart.</summary>
    public int BucketCount { get; init; } = 60;
}

/// <summary>One chart bucket of a log summary (list-pages-server-side plan, Phase 2).</summary>
public sealed class LogSummaryBucket
{
    public DateTime Timestamp { get; init; }
    public long Trace { get; init; }
    public long Debug { get; init; }
    public long Info { get; init; }
    public long Warn { get; init; }
    public long Error { get; init; }
    public long Fatal { get; init; }
}

/// <summary><c>GET /api/logs/summary</c>'s response (Target API): <c>{ source, buckets[], total, totalIsLowerBound, asOf }</c>.</summary>
public sealed class LogSummaryResult
{
    /// <summary>Always <c>"raw"</c> since schema 3.0.0 (there are no rollup tables); kept so the client contract is unchanged.</summary>
    public string Source { get; init; } = "raw";
    public List<LogSummaryBucket> Buckets { get; init; } = [];

    /// <summary>
    /// Exact unless <see cref="TotalIsLowerBound"/>. After a timeout it is a capped count (<see cref="TimedOut"/>): exact
    /// below the cap, the cap itself (lower bound) above it, and 0 (lower bound) when the capped count ran out of time too.
    /// </summary>
    public long Total { get; init; }
    public bool TotalIsLowerBound { get; init; }

    /// <summary>The histogram did not finish within <c>Telemetry:Query:SummaryTimeoutSeconds</c>: <see cref="Buckets"/> is empty.</summary>
    public bool TimedOut { get; init; }
    public DateTime AsOf { get; init; }
}

/// <summary><c>GET /api/logs/page</c>'s response (Target API): <c>{ items[], nextCursor, prevCursor }</c>.</summary>
public sealed class LogPageResult
{
    public List<LogRecordModel> Items { get; init; } = [];
    public string? NextCursor { get; init; }
    public string? PrevCursor { get; init; }
    public DateTime AsOf { get; init; }
}

/// <summary>Filter for <c>GET /api/logs/facets</c> (list-pages-server-side plan, Phase 2, decision 15).</summary>
public sealed class LogFacetsQuery
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string? Service { get; init; }
    public int? MinSeverity { get; init; }
    public string? Search { get; init; }

    /// <summary>Restrict faceting to these attribute keys; null/empty means every key seen in the sample.</summary>
    public IReadOnlyList<string>? Keys { get; init; }

    /// <summary>Top-N values kept per key.</summary>
    public int ValueLimit { get; init; } = 10;
}

public sealed class LogFacetValue
{
    public string Value { get; init; } = "";
    public int Count { get; init; }
}

public sealed class LogFacet
{
    public string Key { get; init; } = "";
    public List<LogFacetValue> Values { get; init; } = [];
}

/// <summary><c>GET /api/logs/facets</c>'s response (Target API): <c>{ sampleSize, facets[] }</c>.</summary>
public sealed class LogFacetsResult
{
    public int SampleSize { get; init; }
    public List<LogFacet> Facets { get; init; } = [];

    /// <summary>
    /// The sample scan ran out of <c>SummaryTimeoutSeconds</c>: <see cref="Facets"/> is empty because the answer is
    /// unknown, not because the matching rows have no attributes.
    /// </summary>
    public bool TimedOut { get; init; }
}

/// <summary>
/// Server-side filter + keyset paging for the traces list page (list-pages-server-side plan,
/// Phase 3). Replaces the offset-paged <c>TraceQuery</c> of the list-page-scale plan: <c>Search</c>
/// carries the raw <c>q</c> text (a <c>tag=key:value</c> token is absorbed into it as a
/// <c>key:value</c> term — Target API), parsed server-side, and paging is keyset
/// (<see cref="Cursor"/>/<see cref="Nav"/>) pinned on <see cref="AsOf"/> (decision 3) rather than
/// offset-based. There is no <c>Sort</c>/<c>Dir</c> (decision 4): every mode pages newest-anchor-first.
/// </summary>
public sealed class TraceQuery
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }

    /// <summary><c>all</c> | <c>errors</c> | <c>slow</c>.</summary>
    public string Mode { get; init; } = "all";

    /// <summary>Exact <c>service.name</c> match, when set.</summary>
    public string? Service { get; init; }

    /// <summary>Only traces whose anchor span has this name (the operation shown on the row), when set (decision 15).</summary>
    public string? Operation { get; init; }

    /// <summary>Minimum trace duration in milliseconds, applied to the anchor span's own duration (decision 11). Only meaningful for <c>slow</c> mode.</summary>
    public double? MinDurationMs { get; init; }

    /// <summary>Maximum trace duration in milliseconds, applied to the anchor span's own duration. Only meaningful for <c>slow</c> mode.</summary>
    public double? MaxDurationMs { get; init; }

    /// <summary>Raw search text: free text, <c>key:value</c>/<c>key=value</c>, negation, trace id — parsed server-side, matched against any span in the whole trace regardless of the service filter (decision 16).</summary>
    public string? Search { get; init; }

    /// <summary>Page size, clamped 1-500 by the repository.</summary>
    public int Size { get; init; } = 100;

    /// <summary>Opaque keyset cursor from a previous page, or null for the first page.</summary>
    public string? Cursor { get; init; }

    /// <summary><c>first</c> | <c>next</c> | <c>prev</c> | <c>last</c> (decision 1).</summary>
    public string Nav { get; init; } = "first";

    /// <summary>The ingestion-time pin (decision 3) — see <see cref="LogQuery.AsOf"/>'s doc comment for the exact same contract.</summary>
    public DateTime? AsOf { get; init; }
}

/// <summary>Filter for <c>GET /api/traces/summary</c> (list-pages-server-side plan, Phase 3, Target API).</summary>
public sealed class TraceSummaryQuery
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }

    /// <summary><c>all</c> | <c>errors</c> | <c>slow</c>.</summary>
    public string Mode { get; init; } = "all";
    public string? Service { get; init; }
    public string? Operation { get; init; }
    public double? MinDurationMs { get; init; }
    public double? MaxDurationMs { get; init; }
    public string? Search { get; init; }
    public DateTime? AsOf { get; init; }

    /// <summary>Target bucket count for the chart.</summary>
    public int BucketCount { get; init; } = 60;

    /// <summary>Latency heatmap row count (time columns come from <see cref="BucketCount"/>).</summary>
    public int LatencyDurationRows { get; init; } = 20;
}

/// <summary><c>GET /api/traces/summary</c>'s response (Target API).</summary>
public sealed class TraceSummaryResult
{
    /// <summary>Always <c>"raw"</c> since schema 3.0.0 (there are no rollup tables); kept so the client contract is unchanged.</summary>
    public string Source { get; init; } = "raw";
    public List<TraceVolumeBucket> Buckets { get; init; } = [];

    /// <summary>Window-wide totals/percentiles over inbound-request anchors (decision 12).</summary>
    public TraceWindowSummary Summary { get; init; } = new();
    public List<ServiceStats> Services { get; init; } = [];
    public List<TraceLatencyBucket> LatencyBuckets { get; init; } = [];

    /// <summary>The paginator's population: every trace's anchor, of any kind (schema-simplification decisions 10 and 12).</summary>
    public long ListTotal { get; init; }

    /// <summary>The cards' population: inbound-request anchors only -- kind SERVER/CONSUMER (decision 12).</summary>
    public long RequestCount { get; init; }

    /// <summary>
    /// <see cref="ListTotal"/> is a lower bound: after a timeout it is a capped count, the cap itself when there are more
    /// anchors than that, or 0 when the capped count ran out of time too.
    /// </summary>
    public bool TotalIsLowerBound { get; init; }

    /// <summary>
    /// The anchor scan did not finish within <c>Telemetry:Query:SummaryTimeoutSeconds</c>: <see cref="Buckets"/>,
    /// <see cref="Summary"/>, <see cref="Services"/> and <see cref="LatencyBuckets"/> are empty and <see cref="RequestCount"/>
    /// is 0, so a reader must not present them as "no traces". <see cref="ListTotal"/> is the capped count.
    /// </summary>
    public bool TimedOut { get; init; }
    public DateTime AsOf { get; init; }
}

/// <summary><c>GET /api/traces/page</c>'s response (Target API): <c>{ items[], nextCursor, prevCursor }</c>.</summary>
public sealed class TracePageResult
{
    public List<TraceInfo> Items { get; init; } = [];
    public string? NextCursor { get; init; }
    public string? PrevCursor { get; init; }
    public DateTime AsOf { get; init; }
}

/// <summary>Filter for <c>GET /api/traces/samples</c> (Target API): the dashboard's Recent Errors/Slowest Traces widgets.</summary>
public sealed class TraceSamplesQuery
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }

    /// <summary><c>errors</c> | <c>slowest</c>.</summary>
    public string Kind { get; init; } = "errors";
    public int Limit { get; init; } = 5;
}

/// <summary>
/// <c>GET /api/traces/samples</c>'s result. The endpoint's body stays a bare array of <see cref="Items"/>;
/// <see cref="TimedOut"/> travels as a response header, so existing consumers of the array are unaffected.
/// </summary>
public sealed class TraceSamplesResult
{
    public List<TraceInfo> Items { get; init; } = [];

    /// <summary>
    /// The anchor scan ran out of <c>SummaryTimeoutSeconds</c>: <see cref="Items"/> is empty because the answer is
    /// unknown, not because the window has no matching traces.
    /// </summary>
    public bool TimedOut { get; init; }
}

/// <summary>One bucket of the trace volume histogram.</summary>
public sealed class TraceVolumeBucket
{
    public DateTime Timestamp { get; init; }
    public int Count { get; init; }
    public int ErrorCount { get; init; }

    /// <summary>Sum of trace durations (ms) in this bucket; avg = SumDurationMs / Count.</summary>
    public double SumDurationMs { get; init; }

    /// <summary>
    /// Duration percentiles (ms) across the traces in this bucket, computed in memory from the
    /// same per-trace durations the histogram already materializes — no extra query. 0 for an
    /// empty bucket (Count == 0); callers should treat that as "no data", not a real value.
    /// </summary>
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
}

/// <summary>
/// One cell of the trace latency chart's time × log-duration grid (trace-latency-p50 plan, Phase
/// 3), mirroring the client's former <c>LatencyBucket</c> in chart.utils.ts. Empty cells are
/// omitted from the result entirely rather than sent as zero-count buckets.
/// </summary>
public sealed class TraceLatencyBucket
{
    public DateTime XStart { get; init; }
    public DateTime XEnd { get; init; }
    public double YStartMs { get; init; }
    public double YEndMs { get; init; }
    public int Count { get; init; }
    public int ErrorCount { get; init; }

    /// <summary>
    /// Set only when <see cref="Count"/> == 1, which is the only case the bubble-click handler
    /// needs a trace id for — a larger bucket is handled by zooming into its time span instead.
    /// Deliberately not a full id list: that would reintroduce an unbounded payload (one id per
    /// trace in the window) for no behavioral gain.
    /// </summary>
    public string? SampleTraceIdHex { get; init; }
}

/// <summary>
/// Window-wide aggregates over the whole filtered trace set — not per bucket and not per
/// service. Exists because neither of those can produce a window percentile: percentiles do not
/// average, so <c>TraceVolumeBucket.P95Ms</c> values cannot be combined into the window's p95,
/// and <see cref="ServiceStats"/> is grouped by service (and carries no p50 at all). Computed
/// from the same already-materialized trace list as the other two groupings — one extra sort,
/// no extra query.
/// </summary>
public sealed class TraceWindowSummary
{
    public int Count { get; init; }
    public int ErrorCount { get; init; }

    /// <summary>
    /// Duration percentiles (ms) across every trace in the window. 0 when <c>Count == 0</c>;
    /// callers should treat that as "no data", not a real value — same contract as
    /// <see cref="TraceVolumeBucket"/>.
    /// </summary>
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
}

/// <summary>One bucket of the log volume-by-severity histogram.</summary>
public sealed class LogVolumeBucket
{
    public DateTime Timestamp { get; init; }
    public int Trace { get; init; }
    public int Debug { get; init; }
    public int Info { get; init; }
    public int Warn { get; init; }
    public int Error { get; init; }
    public int Fatal { get; init; }
}

