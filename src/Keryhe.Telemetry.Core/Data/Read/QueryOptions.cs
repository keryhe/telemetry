using Keryhe.Telemetry.Core.Data;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Query-time bounds for the read path. Bound from the <c>Telemetry:Query</c> configuration
/// section (list-pages-server-side plan, Phase 1) — same convention as
/// <see cref="TelemetryIngestionOptions"/>/<see cref="TraceQueryCacheOptions"/>.
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
    /// Overrides <see cref="ProviderCapabilities.RawSearchWindowHours"/>'s tier default (decision
    /// 40) when set. Null (the default) leaves the tier default in place — analytics-tier
    /// unlimited, standard-tier 24 hours.
    /// </summary>
    public int? RawSearchWindowHoursOverride { get; set; }
}

/// <summary>
/// Bound from the <c>Telemetry:Export</c> configuration section. Currently holds only the
/// <see cref="ProviderCapabilities.ExportMaxWindowDays"/> override (decision 40); export itself
/// ships in Phase 7.
/// </summary>
public sealed class ExportOptions
{
    /// <summary>Configuration section name these options bind from.</summary>
    public const string SectionName = "Telemetry:Export";

    /// <summary>Overrides <see cref="ProviderCapabilities.ExportMaxWindowDays"/>'s tier default (7 analytics / 1 standard) when set.</summary>
    public int? MaxWindowDaysOverride { get; set; }
}
