namespace Keryhe.Telemetry.Collector.Authentication;

/// <summary>Names used to wire the collector's authentication into ASP.NET authorization.</summary>
public static class CollectorAuthorization
{
    /// <summary>
    /// The policy on every collector endpoint: authenticated by the API key scheme and carrying a
    /// tenant claim.
    /// </summary>
    public const string CollectorPolicy = "KeryheTelemetryCollector";
}
