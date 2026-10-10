using Keryhe.Telemetry.Collector.Authentication;

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

    /// <summary>
    /// The largest OTLP export the collector accepts, in bytes, measured after decompression (gRPC's own default and the
    /// OpenTelemetry Collector's, now explicit). A larger one is refused with <c>RESOURCE_EXHAUSTED</c> before it is read into
    /// memory. A client's exporter should batch below it (the OpenTelemetry Collector's <c>batch</c> processor does).
    /// </summary>
    public int MaxReceiveMessageSizeBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// A prefix for the OTLP/HTTP routes (<c>{HttpBasePath}/v1/traces</c>, <c>/v1/logs</c>, <c>/v1/metrics</c>), for a host that mounts them
    /// elsewhere. Empty (the default) serves the specification's paths. A value must start with <c>/</c>.
    /// </summary>
    public string HttpBasePath { get; set; } = "";

    /// <summary>
    /// A connection older than this many seconds (plus up to 10% jitter) is asked to close after its current request, so clients reconnect
    /// and a layer-4 load balancer can spread them over every collector instance. 0 (the default) never closes a healthy connection.
    /// Not needed behind a gRPC-aware (layer 7) balancer or with client-side round-robin.
    /// </summary>
    public int MaxConnectionAgeSeconds { get; set; }

    /// <summary>
    /// Limits on failed authentication attempts per client address. The address is the connection's remote address; behind a reverse proxy
    /// that is the proxy's unless the host configures ASP.NET Core's forwarded-headers middleware with the proxy as a known proxy (the
    /// collector does not trust <c>X-Forwarded-For</c> by itself), and every client behind the proxy then shares one bucket.
    /// </summary>
    public AuthFailureLimitOptions AuthFailureLimit { get; set; } = new();

    /// <summary>
    /// The port of the plaintext management endpoint that serves <c>/healthz/live</c> and <c>/healthz/ready</c> (HTTP/1.1,
    /// for load-balancer and orchestrator probes; the OTLP endpoints are HTTP/2-only). 0 (the default) takes the port of
    /// the Kestrel endpoint named <c>Management</c>, and when there is none the HTTP health endpoints are not mapped
    /// (the gRPC <c>grpc.health.v1.Health</c> service on the OTLP endpoints is always there).
    /// </summary>
    public int ManagementPort { get; set; }

    /// <summary>
    /// Plaintext management addresses allowed outside Development in addition to loopback and private-network ones, as
    /// the exact <c>Url</c> of the Kestrel endpoint (for example <c>http://0.0.0.0:8081</c> in a container whose port is not published).
    /// A plaintext management endpoint on any other address still fails startup.
    /// </summary>
    public string[] ManagementEndpoints { get; set; } = [];
}
