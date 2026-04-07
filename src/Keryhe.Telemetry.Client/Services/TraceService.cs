using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Client.Services;

public class TraceService : ITraceService
{
    private readonly ITraceReadRepository _traceRepository;

    public TraceService(ITraceReadRepository traceRepository)
    {
        _traceRepository = traceRepository ?? throw new ArgumentNullException(nameof(traceRepository));
    }

    public async Task<List<TraceInfo>> GetTracesByTimeRangeAsync(DateTime startTime, DateTime endTime, int limit = 100)
    {
        return await _traceRepository.GetTracesByTimeRangeAsync(startTime, endTime, limit);
    }

    public async Task<List<TraceInfo>> GetTracesByServiceAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, int limit = 100)
    {
        return await _traceRepository.GetTracesByServiceAsync(serviceName, startTime, endTime, limit);
    }

    public async Task<List<TraceInfo>> GetErrorTracesAsync(DateTime? startTime = null, DateTime? endTime = null, int limit = 100)
    {
        return await _traceRepository.GetErrorTracesAsync(startTime, endTime, limit);
    }

    public async Task<List<TraceInfo>> GetSlowTracesAsync(TimeSpan minDuration, DateTime? startTime = null, DateTime? endTime = null, int limit = 100)
    {
        return await _traceRepository.GetSlowTracesAsync(minDuration, startTime, endTime, limit);
    }

    public async Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex)
    {
        return await _traceRepository.GetTraceByIdAsync(traceIdHex);
    }

    public async Task<List<ServiceDependency>> GetServiceDependenciesAsync(DateTime? startTime = null, DateTime? endTime = null)
    {
        return await _traceRepository.GetServiceDependenciesAsync(startTime, endTime);
    }

    public async Task<Dictionary<string, int>> GetOperationCountsAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null)
    {
        return await _traceRepository.GetOperationCountsAsync(serviceName, startTime, endTime);
    }

    public async Task<Dictionary<string, double>> GetAverageLatenciesAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null)
    {
        return await _traceRepository.GetAverageLatenciesAsync(serviceName, startTime, endTime);
    }

    public async Task<List<string>> GetDistinctServicesAsync(DateTime? startTime = null, DateTime? endTime = null)
    {
        // Extract distinct service names from traces
        var traces = await _traceRepository.GetTracesByTimeRangeAsync(
            startTime ?? DateTime.UtcNow.AddDays(-7), 
            endTime ?? DateTime.UtcNow, 
            limit: 1000);
        
        return traces
            .Where(t => !string.IsNullOrEmpty(t.ServiceName))
            .Select(t => t.ServiceName!)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
    }
}
