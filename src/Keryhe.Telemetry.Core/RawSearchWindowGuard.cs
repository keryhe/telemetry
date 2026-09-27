namespace Keryhe.Telemetry.Core;

/// <summary>
/// Enforces <see cref="ProviderCapabilities.RawSearchWindowHours"/> (decision 39): on the standard
/// tier, a logs/traces request carrying a free-text/attribute search or <c>mode=slow</c> over a
/// window longer than the limit is rejected with 400 before any query runs. Analytics-tier
/// providers set no limit, so <see cref="Check"/> always allows them.
///
/// Exemptions (trace-id lookups, <c>mode=errors</c>, metric label filters, the metrics catalog's
/// name search — decision 39) are the CALLER's to express, not this guard's to hardcode: each is a
/// property of a specific endpoint's specific request shape, which phases 2/3 own, not something
/// this Phase-1 guard can enumerate without knowing about endpoints that don't exist yet. A caller
/// exempts a request by passing <c>isExempt: true</c> rather than by this guard special-casing it.
/// Nothing calls this yet — phases 2 and 3 wire it into their endpoints.
/// </summary>
public static class RawSearchWindowGuard
{
    public readonly record struct Result(bool Allowed, string? Message)
    {
        public static readonly Result Ok = new(true, null);
    }

    /// <summary>
    /// Checks whether a request should be rejected. <paramref name="hasRawSearchFilter"/> is true
    /// when the request carries a free-text/attribute search (<c>q</c>) or <c>mode=slow</c>'s
    /// duration filter — the two filter shapes decision 39 bounds. A request with neither (an
    /// unfiltered or service/operation-only request, answered by the summary tables) is never
    /// limited, regardless of window length.
    /// </summary>
    public static Result Check(
        ProviderCapabilities capabilities,
        bool hasRawSearchFilter,
        bool isExempt,
        TimeSpan windowLength)
    {
        if (!hasRawSearchFilter || isExempt)
            return Result.Ok;

        if (capabilities.RawSearchWindowHours is not { } maxHours)
            return Result.Ok;

        if (windowLength <= TimeSpan.FromHours(maxHours))
            return Result.Ok;

        return new Result(false,
            $"Search is limited to a {maxHours}-hour window on {capabilities.Tier} tier. Narrow the time range or remove the search/slow filter.");
    }
}
