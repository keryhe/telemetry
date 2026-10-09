namespace Keryhe.Telemetry.Core.Models;

/// <summary>
/// Server-side filter for the logs list endpoint. <c>Search</c> carries the raw <c>q</c> text, parsed server-side by
/// <see cref="Data.Read.SearchQueryParser"/>. The list is capped, not paged: it returns at most <see cref="Limit"/> rows in
/// <see cref="Order"/> and says whether more matched (<see cref="LogListResult.Truncated"/>).
/// </summary>
public sealed class LogQuery
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }

    /// <summary>Exact <c>service.name</c> match (resource attribute), when set.</summary>
    public string? Service { get; init; }

    /// <summary>Minimum OTLP severity number (inclusive), when set.</summary>
    public int? MinSeverity { get; init; }

    /// <summary>Raw search text: free text, <c>key:value</c>/<c>key=value</c>, negation — parsed server-side.</summary>
    public string? Search { get; init; }

    /// <summary>Most rows to return. The API clamps it to the configured limit; the repository only keeps it at 1 or more.</summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>The limit when none is asked for, and the one a repository used on its own falls back to.</summary>
    public const int DefaultLimit = 1_000;

    /// <summary><c>newest</c> (default) | <c>oldest</c>: which end of the window the rows come from.</summary>
    public string Order { get; init; } = ListOrder.Newest;
}

/// <summary>The values of the <c>order</c> parameter shared by the logs and traces lists.</summary>
public static class ListOrder
{
    public const string Newest = "newest";
    public const string Oldest = "oldest";

    /// <summary>True for <c>oldest</c> (case-insensitive); anything else is newest.</summary>
    public static bool IsOldest(string? order) => string.Equals(order, Oldest, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a value the API accepts: <c>newest</c> or <c>oldest</c>, any case.</summary>
    public static bool IsValid(string? order) => string.Equals(order, Newest, StringComparison.OrdinalIgnoreCase) || IsOldest(order);
}

/// <summary><c>GET .../logs/list</c>'s response: <c>{ items[], truncated }</c>.</summary>
public sealed class LogListResult
{
    public List<LogRecordModel> Items { get; init; } = [];

    /// <summary>More rows matched than <see cref="Items"/> holds.</summary>
    public bool Truncated { get; init; }
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
/// Server-side filter for the traces list endpoint. <c>Search</c> carries the raw <c>q</c> text, parsed server-side.
/// The list is capped, not paged: one row per trace, at most <see cref="Limit"/> of them, from the newest or the oldest end
/// of the window by the trace's anchor start (<see cref="Order"/>), and <see cref="TraceListResult.Truncated"/> says whether
/// more matched.
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

    /// <summary>Most rows to return. The API clamps it to the configured limit; the repository only keeps it at 1 or more.</summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>The limit when none is asked for, and the one a repository used on its own falls back to.</summary>
    public const int DefaultLimit = 500;

    /// <summary><c>newest</c> (default) | <c>oldest</c>: which end of the window the rows come from.</summary>
    public string Order { get; init; } = ListOrder.Newest;
}

/// <summary><c>GET .../traces/list</c>'s response: <c>{ items[], truncated }</c>.</summary>
public sealed class TraceListResult
{
    public List<TraceInfo> Items { get; init; } = [];

    /// <summary>More traces matched than <see cref="Items"/> holds.</summary>
    public bool Truncated { get; init; }
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

