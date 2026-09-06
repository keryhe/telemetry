namespace Keryhe.Telemetry.TestDataGenerator.Generators;

/// <summary>
/// Defines a simulated service for distributed trace generation.
/// </summary>
public class ServiceDefinition
{
    public string Name { get; set; } = "service";
    public string Version { get; set; } = "1.0.0";
    public bool CanBeRootService { get; set; } = true;
    public string[] OperationNames { get; set; } = [];
}
