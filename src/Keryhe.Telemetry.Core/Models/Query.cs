namespace Keryhe.Telemetry.Core.Models;

// =============================================================================
// PAGED READ QUERIES + RESULT ENVELOPE
//
// Server-side pagination/filtering for the logs and traces list pages. The read
// repositories translate these into SQL predicates + LIMIT/OFFSET (logs) or an
// ordered-then-paged projection (traces) and report the full filtered total so the
// UI can render a real "showing X of Y" paginator instead of pulling whole result
// sets into the browser.
// =============================================================================

/// <summary>A single page of results plus the total number of rows that matched the filter.</summary>
public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>Total rows matching the filter across all pages (before <c>Offset</c>/<c>Limit</c>).</summary>
    public int Total { get; init; }

    /// <summary>True when the server stopped short of scanning the full result set (best-effort total).</summary>
    public bool Capped { get; init; }

    public static PagedResult<T> Empty { get; } = new();
}

/// <summary>Server-side filter + paging for the logs list page.</summary>
public sealed class LogQuery
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }

    /// <summary>Exact <c>service.name</c> match (resource attribute), when set.</summary>
    public string? Service { get; init; }

    /// <summary>Minimum OTLP severity number (inclusive), when set.</summary>
    public int? MinSeverity { get; init; }

    /// <summary>Case-insensitive substring match against the log body, when set.</summary>
    public string? Search { get; init; }

    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
}

/// <summary>Server-side filter + paging for the traces list page.</summary>
public sealed class TraceQuery
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }

    /// <summary><c>all</c> | <c>errors</c> | <c>slow</c>.</summary>
    public string Mode { get; init; } = "all";

    /// <summary>Exact <c>service.name</c> match, when set.</summary>
    public string? Service { get; init; }

    /// <summary>Only traces containing a span with this operation (span name), when set.</summary>
    public string? Operation { get; init; }

    /// <summary>Minimum trace duration in milliseconds (only meaningful for <c>slow</c> mode).</summary>
    public double? MinDurationMs { get; init; }

    /// <summary>Maximum trace duration in milliseconds (only meaningful for <c>slow</c> mode).</summary>
    public double? MaxDurationMs { get; init; }

    /// <summary>All-span tag predicates: a trace matches only if <em>some</em> span satisfies each.</summary>
    public IReadOnlyList<TagFilter> Tags { get; init; } = Array.Empty<TagFilter>();

    /// <summary>Sort key: <c>duration</c> | <c>spans</c> | <c>time</c> | <c>service</c> | <c>operation</c>. Null = mode default.</summary>
    public string? Sort { get; init; }

    /// <summary><c>asc</c> | <c>desc</c> (default <c>desc</c>).</summary>
    public string Dir { get; init; } = "desc";

    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
}

/// <summary>A single span-tag predicate for all-span trace search (<c>key:value</c> contains / <c>key=value</c> exact).</summary>
public sealed class TagFilter
{
    public string Key { get; init; } = null!;
    public string Value { get; init; } = "";
    public bool Exact { get; init; }

    /// <summary>Parses one wire token: <c>key=value</c> (exact) or <c>key:value</c> (contains). Null if malformed.</summary>
    public static TagFilter? Parse(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var eq = token.IndexOf('=');
        var colon = token.IndexOf(':');
        // Whichever delimiter appears first wins (values may legitimately contain the other char).
        var exact = eq >= 0 && (colon < 0 || eq < colon);
        var at = exact ? eq : colon;
        if (at <= 0) return null;
        return new TagFilter
        {
            Key = token[..at].Trim(),
            Value = token[(at + 1)..].Trim().Trim('"', '\''),
            Exact = exact
        };
    }
}
