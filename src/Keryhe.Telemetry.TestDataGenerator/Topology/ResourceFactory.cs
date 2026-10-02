using Keryhe.Telemetry.TestDataGenerator.Model;
using OpenTelemetry.Resources;

namespace Keryhe.Telemetry.TestDataGenerator.Topology;

/// <summary>
/// The single definition of a pod's resource attributes. The SDK sink hands the result to the SDK; the OTLP
/// sink copies its attributes into the protobuf, so a backfilled pod and the same pod live are one resource.
/// </summary>
public static class ResourceFactory
{
    public static Resource Build(ServiceInstance i) => Builder(i).Build();

    public static ResourceBuilder Builder(ServiceInstance i) =>
        ResourceBuilder.CreateEmpty()
            .AddService(i.Service, serviceNamespace: i.Namespace, serviceVersion: i.Version,
                autoGenerateServiceInstanceId: false, serviceInstanceId: i.InstanceId)
            .AddTelemetrySdk()
            .AddAttributes(new KeyValuePair<string, object>[]
            {
                new("deployment.environment.name", i.Environment),
                new("host.name", i.HostName),
                new("k8s.namespace.name", i.Namespace),
                new("k8s.pod.name", i.PodName),
                new("k8s.deployment.name", i.Service),
                new("cloud.provider", "aws"),
                new("cloud.region", i.Region),
                new("process.runtime.name", ".NET"),
                new("process.runtime.version", "10.0.0"),
            });
}
