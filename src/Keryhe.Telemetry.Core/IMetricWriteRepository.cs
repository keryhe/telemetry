using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// METRIC WRITE REPOSITORY INTERFACE
// =============================================================================

public interface IMetricWriteRepository
{
    // Store operations
    Task<long> StoreMetricAsync(MetricModel metric, CancellationToken cancellationToken = default);
    Task<IEnumerable<long>> StoreMetricsBatchAsync(IEnumerable<MetricModel> metrics, CancellationToken cancellationToken = default);
}
