using Keryhe.Telemetry.TestDataGenerator.Config;

namespace Keryhe.Telemetry.TestDataGenerator.Clock;

/// <summary>What the active incidents do to one service (and optionally route) at one moment.</summary>
public readonly record struct IncidentEffect(double LatencyMultiplier, double ExtraErrorRate, string? Version)
{
    public static readonly IncidentEffect None = new(1.0, 0.0, null);
}

/// <summary>Recurring daily incidents for one tenant, keyed on UTC time of day.</summary>
public sealed class IncidentSchedule
{
    private readonly IReadOnlyList<IncidentOptions> _incidents;

    public IncidentSchedule(string tenant, IEnumerable<IncidentOptions> all) =>
        _incidents = all.Where(i => i.Tenant is null || string.Equals(i.Tenant, tenant, StringComparison.OrdinalIgnoreCase)).ToList();

    public bool IsEmpty => _incidents.Count == 0;

    public static bool IsActive(IncidentOptions incident, DateTimeOffset t)
    {
        var tod = t.UtcDateTime.TimeOfDay;
        var end = incident.At + incident.Duration;
        // A window that crosses midnight is active either side of it.
        return (tod >= incident.At && tod < end) || (end > TimeSpan.FromDays(1) && tod < end - TimeSpan.FromDays(1));
    }

    /// <summary>The combined effect on <paramref name="service"/>; <paramref name="route"/> null means "any route".</summary>
    public IncidentEffect Effect(string service, string? route, DateTimeOffset t)
    {
        var latency = 1.0;
        var survive = 1.0;
        string? version = null;
        foreach (var i in _incidents)
        {
            if (!string.Equals(i.Service, service, StringComparison.Ordinal)) continue;
            if (!IsActive(i, t)) continue;
            // A version change affects the whole service; latency/errors honor the route filter.
            if (i.Version is not null) version = i.Version;
            if (i.Route is not null && route is not null && !string.Equals(i.Route, route, StringComparison.Ordinal)) continue;
            latency *= i.LatencyMultiplier;
            survive *= 1.0 - i.ExtraErrorRate;
        }
        return new IncidentEffect(latency, 1.0 - survive, version);
    }

    /// <summary>The version a service runs at <paramref name="t"/>, or null for its baseline.</summary>
    public string? VersionOverride(string service, DateTimeOffset t) => Effect(service, null, t).Version;
}
