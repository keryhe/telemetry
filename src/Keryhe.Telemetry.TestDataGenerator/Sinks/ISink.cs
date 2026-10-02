using Keryhe.Telemetry.TestDataGenerator.Model;

namespace Keryhe.Telemetry.TestDataGenerator.Sinks;

/// <summary>Delivers a simulated chunk of one tenant's telemetry to a collector.</summary>
public interface ISink : IAsyncDisposable
{
    Task WriteAsync(SimChunk chunk, CancellationToken cancellationToken);
}
