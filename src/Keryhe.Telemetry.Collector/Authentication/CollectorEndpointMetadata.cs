namespace Keryhe.Telemetry.Collector.Authentication;

/// <summary>
/// Marker added to the three OTLP gRPC endpoints by <c>MapKeryheTelemetryCollector()</c>.
/// <see cref="ApiKeyAuthenticationHandler"/> acts only on endpoints that carry it, so a collector
/// co-hosted with other endpoints (unsupported, but not preventable by the library) never runs its
/// key check, or records its failures, on someone else's requests.
/// </summary>
public sealed class CollectorEndpointMetadata
{
    public static CollectorEndpointMetadata Instance { get; } = new();
}
