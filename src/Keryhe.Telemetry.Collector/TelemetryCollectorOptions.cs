namespace Keryhe.Telemetry.Collector;

/// <summary>Collector transport options. Bound from <c>Telemetry:Collector</c>.</summary>
public sealed class TelemetryCollectorOptions
{
    public const string SectionName = "Telemetry:Collector";

    /// <summary>
    /// Outside Development the collector refuses to start on a plaintext (<c>http://</c>) TCP address,
    /// because an API key sent in cleartext is compromised the moment it is sent. Set true only when
    /// TLS is terminated by a proxy in front of the collector. Unix-socket addresses are exempt.
    /// </summary>
    public bool AllowInsecureTransport { get; set; }
}
