using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// METRICS REPOSITORY INTERFACE
// =============================================================================

public interface IMetricRepository
{
    // Store operations
    Task<long> StoreMetricAsync(MetricModel metric, CancellationToken cancellationToken = default);
    Task<IEnumerable<long>> StoreMetricsBatchAsync(IEnumerable<MetricModel> metrics, CancellationToken cancellationToken = default);
    
    // Retrieve operations
    Task<MetricModel?> GetMetricByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<List<MetricInfo>> GetMetricsByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<List<MetricInfo>> GetMetricsByServiceAsync(string serviceName, CancellationToken cancellationToken = default);
    Task<List<MetricInfo>> GetMetricsByTypeAsync(MetricType type, CancellationToken cancellationToken = default);
    Task<List<MetricInfo>> GetAllMetricsAsync(int limit = 100, CancellationToken cancellationToken = default);
    
    // Time series data
    Task<MetricSeries?> GetMetricSeriesAsync(string metricName, Dictionary<string, string>? labelFilters = null, 
        DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);
    Task<List<MetricSeries>> GetMultipleMetricSeriesAsync(List<string> metricNames, 
        Dictionary<string, string>? labelFilters = null, DateTime? startTime = null, DateTime? endTime = null, 
        CancellationToken cancellationToken = default);
    
    // Aggregation and analysis
    Task<Dictionary<string, double>> GetLatestMetricValuesAsync(string serviceName, CancellationToken cancellationToken = default);
    Task<List<ServiceMetricSummary>> GetServiceMetricSummariesAsync(CancellationToken cancellationToken = default);
    Task<Dictionary<string, int>> GetMetricCountsByTypeAsync(string? serviceName = null, CancellationToken cancellationToken = default);
    Task<List<string>> GetUniqueMetricNamesAsync(string? serviceName = null, CancellationToken cancellationToken = default);
    Task<Dictionary<string, List<string>>> GetMetricLabelsAsync(string metricName, CancellationToken cancellationToken = default);
    
    // Delete operations
    Task<bool> DeleteMetricAsync(long id, CancellationToken cancellationToken = default);
    Task<int> DeleteMetricsByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<int> DeleteMetricsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
    Task<int> DeleteOldMetricsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
}