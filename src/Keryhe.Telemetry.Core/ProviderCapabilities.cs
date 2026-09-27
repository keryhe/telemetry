using Microsoft.Extensions.Configuration;

namespace Keryhe.Telemetry.Core;

/// <summary>Provider tier (decision 39): analytics providers get everything in the plan; standard providers get a bounded subset.</summary>
public enum ProviderTier
{
    Analytics,
    Standard
}

/// <summary>
/// What a provider supports, declared once per provider and exposed to the UI through
/// <c>GET /api/capabilities</c> (list-pages-server-side plan, Phase 1, decision 40). The API
/// enforces the limits from it (via <see cref="RawSearchWindowGuard"/>); the UI reads it to explain
/// a limit up front instead of the request just being slower or erroring unexplained.
/// </summary>
/// <param name="Tier">Analytics: PostgreSQL, Timescale, ClickHouse. Standard: SQL Server, MySQL (decision 39).</param>
/// <param name="IndexedSearch">
/// Whether search (free-text/attribute) runs against dedicated indexes rather than an unindexed
/// scan. True for every analytics-tier provider even though Phase 1 hasn't built those indexes yet
/// (phase 6 does) — this field describes what the TIER supports, not what has shipped so far,
/// which is the more stable meaning for a UI that reads it once at startup and a client that might
/// cache it; the alternative (flip it only once phase 6 lands) would make this same field mean two
/// different things depending on which phase deployed it, for the same provider. Noted here as a
/// deliberate judgment call rather than an unstated assumption.
/// </param>
/// <param name="ExemplarPaging">True when a metric's exemplars page server-side with a real keyset cursor (decision 26); false means the newest-500 fallback.</param>
/// <param name="RawSearchWindowHours">
/// Maximum window, in hours, for a logs/traces request carrying a free-text/attribute search or
/// <c>mode=slow</c>, before the standard tier's <see cref="RawSearchWindowGuard"/> rejects it with
/// 400 (decision 39). Null means no limit (every analytics-tier provider).
/// </param>
/// <param name="ExportMaxWindowDays">Maximum export time window, in days (decision 17): 7 on the analytics tier, 1 on the standard tier by default.</param>
public sealed record ProviderCapabilities(
    ProviderTier Tier,
    bool IndexedSearch,
    bool ExemplarPaging,
    int? RawSearchWindowHours,
    int ExportMaxWindowDays)
{
    /// <summary>Analytics-tier defaults (decision 39), before any <c>Telemetry:Query</c>/<c>Telemetry:Export</c> override is applied.</summary>
    public static ProviderCapabilities AnalyticsDefault() => new(
        Tier: ProviderTier.Analytics,
        IndexedSearch: true,
        ExemplarPaging: true,
        RawSearchWindowHours: null,
        ExportMaxWindowDays: 7);

    /// <summary>Standard-tier defaults (decision 39), before any <c>Telemetry:Query</c>/<c>Telemetry:Export</c> override is applied.</summary>
    public static ProviderCapabilities StandardDefault() => new(
        Tier: ProviderTier.Standard,
        IndexedSearch: false,
        ExemplarPaging: false,
        RawSearchWindowHours: 24,
        ExportMaxWindowDays: 1);

    /// <summary>
    /// Tier defaults, overridden by <c>Telemetry:Query:RawSearchWindowHoursOverride</c> /
    /// <c>Telemetry:Export:MaxWindowDaysOverride</c> when present — called once per provider from
    /// its own <c>Add&lt;Provider&gt;ApiServices</c>. Reads <see cref="IConfiguration"/> directly
    /// (rather than through a bound <see cref="QueryOptions"/>/<c>ExportOptions</c>) since it runs
    /// during service registration, before the options infrastructure it would otherwise depend on
    /// is available to resolve — matching the existing <c>configuration["..."]</c> + <c>TryParse</c>
    /// idiom this codebase already uses at registration time.
    /// </summary>
    public static ProviderCapabilities FromConfiguration(ProviderTier tier, IConfiguration configuration)
    {
        var baseline = tier == ProviderTier.Analytics ? AnalyticsDefault() : StandardDefault();

        var rawSearchOverride = int.TryParse(configuration[$"{Data.Read.QueryOptions.SectionName}:RawSearchWindowHoursOverride"], out var rawSearchHours)
            ? rawSearchHours
            : (int?)null;
        var exportOverride = int.TryParse(configuration[$"{Data.Read.ExportOptions.SectionName}:MaxWindowDaysOverride"], out var exportDays)
            ? exportDays
            : (int?)null;

        return baseline with
        {
            RawSearchWindowHours = rawSearchOverride ?? baseline.RawSearchWindowHours,
            ExportMaxWindowDays = exportOverride ?? baseline.ExportMaxWindowDays
        };
    }
}
