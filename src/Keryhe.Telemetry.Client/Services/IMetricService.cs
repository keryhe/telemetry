using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Client.Services;

public interface IMetricService
{
    Task<List<MetricInfo>> GetAllMetricsAsync(int limit = 100);
    Task<List<MetricInfo>> GetMetricsByNameAsync(string name);
    Task<List<MetricInfo>> GetMetricsByServiceAsync(string serviceName);
    Task<List<MetricInfo>> GetMetricsByTypeAsync(MetricType type);
    Task<List<string>> GetUniqueMetricNamesAsync(string? serviceName = null);
    Task<Dictionary<string, int>> GetMetricCountsByTypeAsync(string? serviceName = null);
    Task<List<ServiceMetricSummary>> GetServiceMetricSummariesAsync();
    Task<MetricSeries?> GetMetricSeriesAsync(string metricName, DateTime? startTime = null, DateTime? endTime = null);
    Task<Dictionary<string, List<string>>> GetMetricLabelsAsync(string metricName);
    Task<Dictionary<string, double>> GetLatestMetricValuesAsync(string serviceName);
}
