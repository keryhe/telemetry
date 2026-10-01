using Microsoft.Extensions.Configuration;

namespace Keryhe.Telemetry.Core;

/// <summary>
/// What a provider supports, declared once per provider and exposed to the UI through
/// <c>GET /api/capabilities</c>. The API enforces the limits from it (via
/// <see cref="RawSearchWindowGuard"/> and <see cref="ExportWindowGuard"/>); the UI reads it to
/// explain a limit up front instead of the request just being slower or erroring unexplained.
///
/// Schema 3.0.0 removed the provider tiers and the indexed-search capability: search is unindexed and
/// time-window bounded on every provider, so there is no longer a tier to report.
/// </summary>
/// <param name="ExemplarPaging">True when a metric's exemplars page server-side with a real keyset cursor; false means the newest-500 fallback.</param>
/// <param name="RawSearchWindowHours">
/// Maximum window, in hours, for a logs/traces request carrying a free-text/attribute search or
/// <c>mode=slow</c>, before <see cref="RawSearchWindowGuard"/> rejects it with 400. Null means no limit.
/// 24 on every provider by default (<see cref="DefaultRawSearchWindowHours"/>).
/// </param>
/// <param name="ExportMaxWindowDays">Maximum export time window, in days: 7 on PostgreSQL/Timescale/ClickHouse and 1 on SQL Server/MySQL by default.</param>
/// <param name="AsOfBackoffSeconds">
/// How far behind the database clock a fresh list/summary query pins <c>asOf</c>
/// (<c>DapperReadRepository.DatabaseClockNowExpr</c>): 5 on PostgreSQL/Timescale, 0 elsewhere. A row
/// is therefore invisible to a pinned list for at least this long after it is written. Informational
/// only; nothing in the API enforces it. The stress harness subtracts it from its log-lag probe.
/// </param>
public sealed record ProviderCapabilities(
    bool ExemplarPaging,
    int? RawSearchWindowHours,
    int ExportMaxWindowDays,
    int AsOfBackoffSeconds = 0)
{
    /// <summary>Search is limited to this many hours on every provider unless <c>Telemetry:Query:RawSearchWindowHoursOverride</c> says otherwise.</summary>
    public const int DefaultRawSearchWindowHours = 24;

    /// <summary>Defaults for a provider with keyset exemplar paging and a 7-day export window (PostgreSQL, Timescale, ClickHouse).</summary>
    public static ProviderCapabilities Default() => new(
        ExemplarPaging: true,
        RawSearchWindowHours: DefaultRawSearchWindowHours,
        ExportMaxWindowDays: 7);

    /// <summary>Defaults for a provider with the newest-500 exemplar fallback and a 1-day export window (SQL Server, MySQL).</summary>
    public static ProviderCapabilities Constrained() => new(
        ExemplarPaging: false,
        RawSearchWindowHours: DefaultRawSearchWindowHours,
        ExportMaxWindowDays: 1);

    /// <summary>
    /// The provider's defaults, overridden by <c>Telemetry:Query:RawSearchWindowHoursOverride</c> /
    /// <c>Telemetry:Export:MaxWindowDaysOverride</c> when present -- called once per provider from its
    /// own <c>Add&lt;Provider&gt;ApiServices</c>. Reads <see cref="IConfiguration"/> directly (rather than
    /// through a bound <see cref="Data.Read.QueryOptions"/>/<c>ExportOptions</c>) since it runs during
    /// service registration, before the options infrastructure is available to resolve.
    /// </summary>
    public static ProviderCapabilities FromConfiguration(ProviderCapabilities defaults, IConfiguration configuration)
    {
        var rawSearchOverride = int.TryParse(configuration[$"{Data.Read.QueryOptions.SectionName}:RawSearchWindowHoursOverride"], out var rawSearchHours)
            ? rawSearchHours
            : (int?)null;
        var exportOverride = int.TryParse(configuration[$"{Data.Read.ExportOptions.SectionName}:MaxWindowDaysOverride"], out var exportDays)
            ? exportDays
            : (int?)null;

        return defaults with
        {
            RawSearchWindowHours = rawSearchOverride ?? defaults.RawSearchWindowHours,
            ExportMaxWindowDays = exportOverride ?? defaults.ExportMaxWindowDays
        };
    }
}
