using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// TRACE READ REPOSITORY INTERFACE
// =============================================================================

public interface ITraceReadRepository
{
    // Retrieve operations
    Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex, CancellationToken cancellationToken = default);
    Task<SpanModel?> GetSpanByIdAsync(string traceIdHex, string spanIdHex, CancellationToken cancellationToken = default);
    Task<List<SpanModel>> GetSpansByParentAsync(string traceIdHex, string parentSpanIdHex, CancellationToken cancellationToken = default);
    Task<List<TraceInfo>> GetTracesByTimeRangeAsync(DateTime startTime, DateTime endTime, int limit = 100, CancellationToken cancellationToken = default);
    Task<List<TraceInfo>> GetTracesByServiceAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<List<TraceInfo>> GetErrorTracesAsync(DateTime? startTime = null, DateTime? endTime = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<List<TraceInfo>> GetSlowTracesAsync(TimeSpan minDuration, DateTime? startTime = null, DateTime? endTime = null, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Server-side filtered + paged trace query; returns a page of traces plus the full filtered total.</summary>
    Task<PagedResult<TraceInfo>> QueryTracesAsync(TraceQuery query, CancellationToken cancellationToken = default);

    /// <summary>True volume histogram (fixed bucket count over the full filtered range) for the traces list/dashboard chart, unaffected by any row-count cap.</summary>
    Task<List<TraceVolumeBucket>> GetTraceHistogramAsync(HistogramQuery query, CancellationToken cancellationToken = default);

    // Analysis operations
    Task<List<ServiceDependency>> GetServiceDependenciesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);
    Task<Dictionary<string, int>> GetOperationCountsAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);
    Task<Dictionary<string, double>> GetAverageLatenciesAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);

    /// <summary>Per-operation RED metrics (rate, error%, p50/p95/p99, avg) for a service over the window.</summary>
    Task<List<OperationStats>> GetOperationStatsAsync(string serviceName, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
}
