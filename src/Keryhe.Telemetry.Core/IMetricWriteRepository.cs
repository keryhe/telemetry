using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// METRIC WRITE REPOSITORY INTERFACE
// =============================================================================

public interface IMetricWriteRepository
{
    // Store operations
    Task<long> StoreMetricAsync(MetricModel metric, CancellationToken cancellationToken = default);
    /// <param name="requestBytes">The protobuf size of the export these metrics came from, reserved on the queue's byte budget (0 = not measured).</param>
    Task<IEnumerable<long>> StoreMetricsBatchAsync(IEnumerable<MetricModel> metrics, CancellationToken cancellationToken = default, long requestBytes = 0);
}
