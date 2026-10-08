using Keryhe.Telemetry.Core.Data.Read;
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
/// <param name="RawSearchWindowHours">
/// Maximum window, in hours, for a logs/traces request carrying a free-text/attribute search or
/// <c>mode=slow</c>, before <see cref="RawSearchWindowGuard"/> rejects it with 400. Null means no limit.
/// 24 on every provider by default (<see cref="DefaultRawSearchWindowHours"/>).
/// </param>
/// <param name="ExportMaxWindowDays">Maximum export time window, in days: 7 on PostgreSQL/ClickHouse and 1 on SQL Server/MySQL by default.</param>
/// <param name="Limits">The most rows each capped list returns (<c>Telemetry:Query:Limits</c>); the API clamps a request's <c>limit</c> to them and the UI prints them.</param>
public sealed record ProviderCapabilities(
    int? RawSearchWindowHours,
    int ExportMaxWindowDays,
    QueryLimitsOptions Limits)
{
    /// <summary>Search is limited to this many hours on every provider unless <c>Telemetry:Query:RawSearchWindowHoursOverride</c> says otherwise.</summary>
    public const int DefaultRawSearchWindowHours = 24;

    /// <summary>Defaults for a provider with a 7-day export window (PostgreSQL, ClickHouse).</summary>
    public static ProviderCapabilities Default() => new(
        RawSearchWindowHours: DefaultRawSearchWindowHours,
        ExportMaxWindowDays: 7,
        Limits: new QueryLimitsOptions());

    /// <summary>Defaults for a provider with a 1-day export window (SQL Server, MySQL).</summary>
    public static ProviderCapabilities Constrained() => new(
        RawSearchWindowHours: DefaultRawSearchWindowHours,
        ExportMaxWindowDays: 1,
        Limits: new QueryLimitsOptions());

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
            ExportMaxWindowDays = exportOverride ?? defaults.ExportMaxWindowDays,
            Limits = QueryLimitsOptions.FromConfiguration(configuration)
        };
    }
}
