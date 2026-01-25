using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Client.Services;

public interface ITraceService
{
    Task<List<TraceInfo>> GetTracesByTimeRangeAsync(DateTime startTime, DateTime endTime, int limit = 100);
    Task<List<TraceInfo>> GetTracesByServiceAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, int limit = 100);
    Task<List<TraceInfo>> GetErrorTracesAsync(DateTime? startTime = null, DateTime? endTime = null, int limit = 100);
    Task<List<TraceInfo>> GetSlowTracesAsync(TimeSpan minDuration, DateTime? startTime = null, DateTime? endTime = null, int limit = 100);
    Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex);
    Task<List<ServiceDependency>> GetServiceDependenciesAsync(DateTime? startTime = null, DateTime? endTime = null);
    Task<Dictionary<string, int>> GetOperationCountsAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null);
    Task<Dictionary<string, double>> GetAverageLatenciesAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null);
    Task<List<string>> GetDistinctServicesAsync(DateTime? startTime = null, DateTime? endTime = null);
}
