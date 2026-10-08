using Keryhe.Telemetry.Core.Data;
using Microsoft.Extensions.Configuration;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Query-time bounds for the read path. Bound from the <c>Telemetry:Query</c> configuration
/// section (list-pages-server-side plan, Phase 1) — same convention as
/// <see cref="TelemetryIngestionOptions"/>.
/// </summary>
public sealed class QueryOptions
{
    /// <summary>Configuration section name these options bind from.</summary>
    public const string SectionName = "Telemetry:Query";

    /// <summary>
    /// Statement timeout, in seconds, for a summary/count query before it falls back to a lower
    /// bound (decision 11) — "exact, bounded by a 5s statement timeout, falling back to '≥ N'".
    /// Also the timeout for a metric series/exemplar query (decision 31). Defaults to 5.
    /// </summary>
    public int SummaryTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Overrides the default search window (<see cref="ProviderCapabilities.DefaultRawSearchWindowHours"/>,
    /// 24 hours on every provider) when set. Search is unindexed everywhere since schema 3.0.0, so it
    /// is time-window bounded everywhere.
    /// </summary>
    public int? RawSearchWindowHoursOverride { get; set; }

    /// <summary>The default for <see cref="AnchorLookbackMinutes"/>.</summary>
    public const int DefaultAnchorLookbackMinutes = 5;

    /// <summary>
    /// How far before a trace-list window's start the trace anchor derivation looks (schema-simplification
    /// decision 17): a trace anchors on its earliest span in scope, so without this margin a trace that
    /// began just before the window would be anchored on a later span and listed. A trace that started
    /// before the margin is not listed in the window (it belongs to an earlier one). Default 5.
    /// </summary>
    public int AnchorLookbackMinutes { get; set; } = DefaultAnchorLookbackMinutes;

    /// <summary>The default for <see cref="PageSliceSeconds"/>.</summary>
    public const int DefaultPageSliceSeconds = 2;

    /// <summary>The default for <see cref="PageSliceGrowth"/>.</summary>
    public const int DefaultPageSliceGrowth = 4;

    /// <summary>
    /// The first slice, in seconds of trace start time, that a trace-list page scans (trace-list-detail-performance
    /// plan, Phase 4). A page needs only <c>size + 1</c> anchors, so instead of ranking every trace in the window it reads
    /// the newest (or, paging back, the next) slice and widens it until the page is full: the cost follows the page size
    /// and the traffic density, not the window. Default 2.
    /// </summary>
    public int PageSliceSeconds { get; set; } = DefaultPageSliceSeconds;

    /// <summary>The factor each further slice is wider than the last (at least 2). Default 4.</summary>
    public int PageSliceGrowth { get; set; } = DefaultPageSliceGrowth;

    /// <summary>The default for <see cref="TraceHintMarginMinutes"/>.</summary>
    public const int DefaultTraceHintMarginMinutes = 1;

    /// <summary>
    /// How far either side of a trace-detail time hint (the trace's start and end as the list returned them) the bounded read looks
    /// (trace-list-detail-performance plan, Phase 6). Kept small on purpose: a wide range gives the PostgreSQL planner a competing
    /// time-index path, and on a chunk that is still being written its statistics are stale. Default 1.
    /// </summary>
    public int TraceHintMarginMinutes { get; set; } = DefaultTraceHintMarginMinutes;

    /// <summary>False ignores every trace-detail time hint: the read is then unbounded on every provider. Default true.</summary>
    public bool TraceHintEnabled { get; set; } = true;
}

/// <summary>
/// The most rows each capped list returns, from <c>Telemetry:Query:Limits</c>. A request may ask for fewer, never more.
/// Read once at registration by <see cref="ProviderCapabilities.FromConfiguration"/>, which also reports them to the UI
/// through <c>GET /api/capabilities</c>.
/// </summary>
public sealed class QueryLimitsOptions
{
    /// <summary>Configuration section name these options bind from.</summary>
    public const string SectionName = QueryOptions.SectionName + ":Limits";

    /// <summary>Logs list. Default 1,000.</summary>
    public int Logs { get; set; } = 1_000;

    /// <summary>Traces list. Default 500.</summary>
    public int Traces { get; set; } = 500;

    /// <summary>Metrics catalog, in either grouping. Default 500.</summary>
    public int MetricCatalog { get; set; } = 500;

    /// <summary>A metric's exemplars. Default 500.</summary>
    public int Exemplars { get; set; } = 500;

    /// <summary>Reads the section; a value that is present but not a positive integer fails startup naming its key.</summary>
    public static QueryLimitsOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new QueryLimitsOptions();
        options.Logs = Read(configuration, nameof(Logs), options.Logs);
        options.Traces = Read(configuration, nameof(Traces), options.Traces);
        options.MetricCatalog = Read(configuration, nameof(MetricCatalog), options.MetricCatalog);
        options.Exemplars = Read(configuration, nameof(Exemplars), options.Exemplars);
        return options;
    }

    private static int Read(IConfiguration configuration, string name, int fallback)
    {
        var key = $"{SectionName}:{name}";
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, out var value) || value < 1)
            throw new InvalidOperationException($"{key} must be a positive integer (was '{raw}').");
        return value;
    }
}

/// <summary>
/// Bound from the <c>Telemetry:Export</c> configuration section (list-pages-server-side plan,
/// Phase 8). Holds the <see cref="ProviderCapabilities.ExportMaxWindowDays"/> override (decision
/// 40, wired in Phase 1) plus <see cref="MaxConcurrent"/> (decision 17), which bounds how many
/// <c>/api/*/export</c> requests may stream at once per API instance.
/// </summary>
public sealed class ExportOptions
{
    /// <summary>Configuration section name these options bind from.</summary>
    public const string SectionName = "Telemetry:Export";

    /// <summary>Overrides <see cref="ProviderCapabilities.ExportMaxWindowDays"/>'s tier default (7 days on PostgreSQL/ClickHouse, 1 on SQL Server/MySQL) when set.</summary>
    public int? MaxWindowDaysOverride { get; set; }

    /// <summary>
    /// Maximum number of exports (logs, traces or metrics combined) streaming at once per API
    /// instance (decision 17); a request beyond this gets <c>429</c>. Each export holds a pooled
    /// database connection for its whole duration (a SQL Server export holds it under
    /// <c>SNAPSHOT</c>, decision 35), so this is also an effective cap on export connections held
    /// out of the pool. Default 2.
    /// </summary>
    public int MaxConcurrent { get; set; } = 2;
}
