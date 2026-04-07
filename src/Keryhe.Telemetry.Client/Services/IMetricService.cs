using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Client.Services;

public interface IMetricService
{
    Task<List<MetricInfo>> GetAllMetricsAsync(int limit = 100, DateTime? startTime = null, DateTime? endTime = null);
    Task<List<MetricInfo>> GetMetricsByNameAsync(string name);
    Task<List<MetricInfo>> GetMetricsByServiceAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null);
    Task<List<MetricInfo>> GetMetricsByTypeAsync(MetricType type);
    Task<List<string>> GetUniqueMetricNamesAsync(string? serviceName = null);
    Task<Dictionary<string, int>> GetMetricCountsByTypeAsync(string? serviceName = null);
    Task<List<ServiceMetricSummary>> GetServiceMetricSummariesAsync();
    Task<MetricSeries?> GetMetricSeriesAsync(string metricName, Dictionary<string, string>? labelFilters = null, DateTime? startTime = null, DateTime? endTime = null, long? metricId = null);
    Task<Dictionary<string, List<string>>> GetMetricLabelsAsync(string metricName);
    Task<Dictionary<string, double>> GetLatestMetricValuesAsync(string serviceName);
    Task<MultiSeriesMetricData?> GetMetricSeriesByServiceAsync(string metricName, DateTime? startTime = null, DateTime? endTime = null);
}
