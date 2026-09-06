using System.Diagnostics;

namespace Keryhe.Telemetry.TestDataGenerator.Generators;

/// <summary>
/// Pairs an <see cref="ActivitySource"/> with its <see cref="ServiceDefinition"/>
/// so the trace generator can emit spans attributed to the correct service.
/// </summary>
public class ServiceSource
{
    public ActivitySource ActivitySource { get; init; } = null!;
    public ServiceDefinition Definition { get; init; } = null!;
}
