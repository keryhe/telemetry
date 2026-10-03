using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Collector.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Endpoint routing extensions for the Keryhe Telemetry collector.
/// </summary>
public static class TelemetryCollectorEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the three OTLP gRPC services. Requires <c>AddKeryheTelemetryCollector()</c>
    /// and an endpoint that negotiates HTTP/2. The host must also call <c>UseAuthentication()</c> and
    /// <c>UseAuthorization()</c> after <c>UseRouting()</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapKeryheTelemetryCollector(this IEndpointRouteBuilder endpoints)
    {
        // Each service carries the marker (so the API key handler acts on it) and requires the collector
        // policy: a rejected call never reaches the service, so its body is never deserialized.
        endpoints.MapGrpcService<LogService>().Collector();
        endpoints.MapGrpcService<TraceService>().Collector();
        endpoints.MapGrpcService<MetricService>().Collector();
        return endpoints;
    }

    private static GrpcServiceEndpointConventionBuilder Collector(this GrpcServiceEndpointConventionBuilder builder)
    {
        builder.Add(b => b.Metadata.Add(CollectorEndpointMetadata.Instance));
        builder.RequireAuthorization(CollectorAuthorization.CollectorPolicy);
        return builder;
    }
}
