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

    /// <summary>
    /// Chart/stat-card summary for the traces list page (list-pages-server-side plan, Phase 3):
    /// volume/error/duration-percentile buckets, per-service RED stats, the latency heatmap and
    /// <c>listTotal</c>/<c>requestCount</c> (decision 13). Reads the trace rollup tables when
    /// eligible (decision 37), the anchor-bounded raw path otherwise. <c>listTotal</c> always
    /// comes from the raw path.
    /// </summary>
    Task<TraceSummaryResult> GetTraceSummaryAsync(TraceSummaryQuery query, CancellationToken cancellationToken = default);

    /// <summary>Keyset-paged trace rows for the traces list page (decision 1), anchored on roots plus <c>orphan_roots</c> (decision 41), pinned on <see cref="TraceQuery.AsOf"/> (decision 3).</summary>
    Task<TracePageResult> GetTracePageAsync(TraceQuery query, CancellationToken cancellationToken = default);

    /// <summary>The dashboard's Recent Errors/Slowest Traces widgets — newest errors or the slowest anchors, over the unfiltered population.</summary>
    Task<List<TraceInfo>> GetTraceSamplesAsync(TraceSamplesQuery query, CancellationToken cancellationToken = default);

    // Analysis operations
    Task<List<ServiceDependency>> GetServiceDependenciesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);
    Task<Dictionary<string, int>> GetOperationCountsAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);
    Task<Dictionary<string, double>> GetAverageLatenciesAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);

    /// <summary>Per-operation RED metrics (rate, error%, p50/p95/p99, avg) for a service over the window.</summary>
    Task<List<OperationStats>> GetOperationStatsAsync(string serviceName, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming export (list-pages-server-side plan, Phase 8, decision 17): one <see cref="TraceInfo"/>
    /// row per trace matching the same filters as <see cref="GetTraceSummaryAsync"/>/
    /// <see cref="GetTracePageAsync"/>, with no row cap. Span-level export is out of scope. Memory
    /// stays bounded to one internal chunk at a time (see <c>TraceReadRepositoryBase</c>'s own doc
    /// comment on this method for why it chunks rather than issuing one unbuffered query), not the
    /// whole matching population.
    /// </summary>
    IAsyncEnumerable<TraceInfo> ExportTracesAsync(TraceExportQuery query, CancellationToken cancellationToken = default);
}
