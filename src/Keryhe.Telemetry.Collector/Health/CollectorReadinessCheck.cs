using Keryhe.Telemetry.Core.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Collector.Health;

/// <summary>
/// Ready means this instance should be sent traffic: not draining for shutdown, the control plane is answering the key
/// lookups it has had to make, and no signal's queue has been full for longer than a flush could explain. A balancer that
/// routes around a not-ready instance spreads load; the readiness thresholds are on <see cref="TelemetryIngestionOptions"/>.
/// </summary>
public sealed class CollectorReadinessCheck(
    TelemetryIngestionChannel channel,
    ControlPlaneHealth controlPlane,
    IOptions<TelemetryIngestionOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        if (channel.IsShuttingDown)
            return Task.FromResult(HealthCheckResult.Unhealthy("The collector is shutting down"));

        var failing = controlPlane.FailingFor;
        if (failing > TimeSpan.FromSeconds(o.ReadinessControlPlaneSeconds))
            return Task.FromResult(HealthCheckResult.Unhealthy($"API-key lookups have been failing for {failing.TotalSeconds:0} s (the control plane is unreachable)"));

        var saturated = channel.LongestSaturation;
        if (saturated > TimeSpan.FromSeconds(o.ReadinessSaturatedSeconds))
            return Task.FromResult(HealthCheckResult.Unhealthy($"An ingestion queue has been full for {saturated.TotalSeconds:0} s"));

        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
