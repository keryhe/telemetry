using Keryhe.Telemetry.Collector;
using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Collector.Http;
using Keryhe.Telemetry.Collector.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        // OTLP/HTTP, behind the same policy and handler.
        var basePath = (endpoints.ServiceProvider.GetRequiredService<IOptions<TelemetryCollectorOptions>>().Value.HttpBasePath ?? "").Trim().TrimEnd('/');
        if (basePath.Length > 0 && !basePath.StartsWith('/'))
            throw new InvalidOperationException($"Telemetry:Collector:HttpBasePath must start with '/' (was '{basePath}').");
        OtlpHttpEndpoints.Map(endpoints, basePath);

        // Health needs no API key: a probe is not a tenant. gRPC health rides the OTLP endpoints; the HTTP endpoints only
        // answer on the management port (HTTP/1.1; the OTLP endpoints are HTTP/2-only, which plain probes do not speak).
        endpoints.MapGrpcHealthChecksService();
        var port = ManagementPort(endpoints.ServiceProvider);
        if (port > 0)
        {
            var host = $"*:{port}";
            endpoints.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false }).RequireHost(host);
            endpoints.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).RequireHost(host);
        }
        return endpoints;
    }

    private static int ManagementPort(IServiceProvider services)
    {
        var configured = services.GetRequiredService<IOptions<TelemetryCollectorOptions>>().Value.ManagementPort;
        if (configured > 0) return configured;
        var url = services.GetRequiredService<IConfiguration>().GetSection("Kestrel:Endpoints").GetChildren()
            .FirstOrDefault(e => string.Equals(e.Key, "Management", StringComparison.OrdinalIgnoreCase))?["Url"];
        return url is not null && Uri.TryCreate(url.Replace("*", "wildcard").Replace("+", "wildcard"), UriKind.Absolute, out var uri) ? uri.Port : 0;
    }

    private static GrpcServiceEndpointConventionBuilder Collector(this GrpcServiceEndpointConventionBuilder builder)
    {
        builder.Add(b => b.Metadata.Add(CollectorEndpointMetadata.Instance));
        builder.RequireAuthorization(CollectorAuthorization.CollectorPolicy);
        return builder;
    }
}
