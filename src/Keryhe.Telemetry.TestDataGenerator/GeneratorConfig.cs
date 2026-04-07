namespace Keryhe.Telemetry.TestDataGenerator;

/// <summary>
/// Configuration for the test data generator.
/// </summary>
public class GeneratorConfig
{
    public string ServiceName { get; set; } = "telemetry-test-generator";
    public string ServiceVersion { get; set; } = "1.0.0";
    public string OtlpEndpoint { get; set; } = "http://localhost:5117";
    public string GeneratorMode { get; set; } = "Realistic"; // "Realistic" or "LoadTest"
    public int EmissionIntervalSeconds { get; set; } = 15;
    public int SpansPerBatch { get; set; } = 8;
    public int MetricsPerBatch { get; set; } = 4;
    public int LogsPerBatch { get; set; } = 3;
}
