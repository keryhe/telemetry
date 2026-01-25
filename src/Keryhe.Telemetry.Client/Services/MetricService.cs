using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Client.Services;

public class MetricService : IMetricService
{
    private readonly IMetricRepository _metricRepository;

    public MetricService(IMetricRepository metricRepository)
    {
        _metricRepository = metricRepository ?? throw new ArgumentNullException(nameof(metricRepository));
    }

    public async Task<List<MetricInfo>> GetAllMetricsAsync(int limit = 100)
    {
        return await _metricRepository.GetAllMetricsAsync(limit);
    }

    public async Task<List<MetricInfo>> GetMetricsByNameAsync(string name)
    {
        return await _metricRepository.GetMetricsByNameAsync(name);
    }

    public async Task<List<MetricInfo>> GetMetricsByServiceAsync(string serviceName)
    {
        return await _metricRepository.GetMetricsByServiceAsync(serviceName);
    }

    public async Task<List<MetricInfo>> GetMetricsByTypeAsync(MetricType type)
    {
        return await _metricRepository.GetMetricsByTypeAsync(type);
    }

    public async Task<List<string>> GetUniqueMetricNamesAsync(string? serviceName = null)
    {
        return await _metricRepository.GetUniqueMetricNamesAsync(serviceName);
    }

    public async Task<Dictionary<string, int>> GetMetricCountsByTypeAsync(string? serviceName = null)
    {
        return await _metricRepository.GetMetricCountsByTypeAsync(serviceName);
    }

    public async Task<List<ServiceMetricSummary>> GetServiceMetricSummariesAsync()
    {
        return await _metricRepository.GetServiceMetricSummariesAsync();
    }

    public async Task<MetricSeries?> GetMetricSeriesAsync(string metricName, DateTime? startTime = null, DateTime? endTime = null)
    {
        return await _metricRepository.GetMetricSeriesAsync(metricName, null, startTime, endTime);
    }

    public async Task<Dictionary<string, List<string>>> GetMetricLabelsAsync(string metricName)
    {
        return await _metricRepository.GetMetricLabelsAsync(metricName);
    }

    public async Task<Dictionary<string, double>> GetLatestMetricValuesAsync(string serviceName)
    {
        return await _metricRepository.GetLatestMetricValuesAsync(serviceName);
    }
}
