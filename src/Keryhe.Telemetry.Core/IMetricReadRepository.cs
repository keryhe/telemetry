using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// METRIC READ REPOSITORY INTERFACE
// =============================================================================

public interface IMetricReadRepository
{
    // Retrieve operations
    Task<MetricModel?> GetMetricByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<List<MetricInfo>> GetMetricsByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<List<MetricInfo>> GetMetricsByTypeAsync(MetricType type, CancellationToken cancellationToken = default);
    Task<List<MetricInfo>> GetAllMetricsAsync(int limit = 100, DateTime? startTime = null,
        DateTime? endTime = null, CancellationToken cancellationToken = default);

    // Time series data
    Task<MetricSeries?> GetMetricSeriesAsync(string metricName, Dictionary<string, string>? labelFilters = null,
        DateTime? startTime = null, DateTime? endTime = null, long? metricId = null,
        CancellationToken cancellationToken = default);
    Task<List<MetricSeries>> GetMultipleMetricSeriesAsync(List<string> metricNames,
        Dictionary<string, string>? labelFilters = null, DateTime? startTime = null, DateTime? endTime = null,
        CancellationToken cancellationToken = default);
    Task<MultiSeriesMetricData?> GetMetricSeriesByServiceAsync(string metricName,
        DateTime? startTime = null, DateTime? endTime = null,
        CancellationToken cancellationToken = default);
    Task<MultiSeriesMetricData?> GetGroupedMetricSeriesAsync(string metricName,
        DateTime? startTime = null, DateTime? endTime = null, long? metricId = null,
        Dictionary<string, string>? labelFilters = null,
        CancellationToken cancellationToken = default);

    // Aggregation and analysis
    Task<Dictionary<string, double>> GetLatestMetricValuesAsync(string serviceName, CancellationToken cancellationToken = default);
    Task<Dictionary<string, int>> GetMetricCountsByTypeAsync(string? serviceName = null, CancellationToken cancellationToken = default);
    Task<List<string>> GetUniqueMetricNamesAsync(string? serviceName = null, CancellationToken cancellationToken = default);
    Task<Dictionary<string, List<string>>> GetMetricLabelsAsync(string metricName, CancellationToken cancellationToken = default);
    Task<List<string>> GetDistinctServicesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);
}
