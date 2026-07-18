using Keryhe.Telemetry.Collector.Services;
using Microsoft.AspNetCore.Routing;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Endpoint routing extensions for the Keryhe Telemetry collector.
/// </summary>
public static class TelemetryCollectorEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the three OTLP gRPC services. Requires <c>AddKeryheTelemetryCollector()</c>
    /// and an endpoint that negotiates HTTP/2.
    /// </summary>
    public static IEndpointRouteBuilder MapKeryheTelemetryCollector(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<LogService>();
        endpoints.MapGrpcService<TraceService>();
        endpoints.MapGrpcService<MetricService>();
        return endpoints;
    }
}
