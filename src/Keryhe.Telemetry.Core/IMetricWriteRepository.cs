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

    // Delete operations
    Task<bool> DeleteMetricAsync(long id, CancellationToken cancellationToken = default);
    Task<int> DeleteMetricsByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<int> DeleteMetricsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
    Task<int> DeleteOldMetricsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
}
