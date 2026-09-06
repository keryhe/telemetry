using Keryhe.Telemetry.TestDataGenerator.Generators;

namespace Keryhe.Telemetry.TestDataGenerator;

/// <summary>
/// Configuration for the test data generator.
/// </summary>
public class GeneratorConfig
{
    public string ServiceName { get; set; } = "telemetry-test-generator";
    public string ServiceVersion { get; set; } = "1.0.0";
    public string OtlpEndpoint { get; set; } = "http://localhost:5117";
    public string OtlpHeaders { get; set; } = "";
    public string GeneratorMode { get; set; } = "Realistic"; // "Realistic" or "LoadTest"
    public int EmissionIntervalSeconds { get; set; } = 15;
    public int SpansPerBatch { get; set; } = 8;
    public int MetricsPerBatch { get; set; } = 4;
    public int LogsPerBatch { get; set; } = 3;

    // LoadTest mode overrides
    public int LoadTestEmissionIntervalSeconds { get; set; } = 5;
    public int LoadTestSpansPerBatch { get; set; } = 50;
    public int LoadTestMetricsPerBatch { get; set; } = 25;
    public int LoadTestLogsPerBatch { get; set; } = 15;

    /// <summary>
    /// Optional list of simulated services for distributed trace generation.
    /// When populated, traces will span multiple services using context propagation.
    /// When empty, falls back to single-service mode using <see cref="ServiceName"/>.
    /// </summary>
    public List<ServiceDefinition> Services { get; set; } = [];
}
