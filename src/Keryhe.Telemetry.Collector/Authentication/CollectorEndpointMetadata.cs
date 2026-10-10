namespace Keryhe.Telemetry.Collector.Authentication;

/// <summary>The transport an OTLP endpoint speaks, which decides how a rejection is written.</summary>
public enum CollectorProtocol { Grpc, Http }

/// <summary>
/// Marker added to the OTLP endpoints (the three gRPC services and the three HTTP routes) by <c>MapKeryheTelemetryCollector()</c>.
/// <see cref="ApiKeyAuthenticationHandler"/> acts only on endpoints that carry it, so a collector
/// co-hosted with other endpoints (unsupported, but not preventable by the library) never runs its
/// key check, or records its failures, on someone else's requests. <see cref="Protocol"/> tells the handler whether to write a
/// trailers-only gRPC response or an HTTP status with a <c>google.rpc.Status</c> body.
/// </summary>
public sealed class CollectorEndpointMetadata(CollectorProtocol protocol)
{
    public CollectorProtocol Protocol { get; } = protocol;

    /// <summary>The gRPC marker (kept as the default for existing callers).</summary>
    public static CollectorEndpointMetadata Instance { get; } = new(CollectorProtocol.Grpc);

    public static CollectorEndpointMetadata Http { get; } = new(CollectorProtocol.Http);
}
