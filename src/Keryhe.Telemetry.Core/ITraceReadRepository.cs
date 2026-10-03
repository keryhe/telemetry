using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// TRACE READ REPOSITORY INTERFACE
// =============================================================================

public interface ITraceReadRepository
{
    // Retrieve operations
    Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="GetTraceByIdAsync(string, CancellationToken)"/> with a time hint: the trace's extent as the trace list returned it
    /// (<see cref="TraceInfo.TraceStartTime"/>/<see cref="TraceInfo.TraceEndTime"/>). A provider that cannot seek a trace id
    /// (Timescale's chunks, ClickHouse's sort key) reads only that range, widened by <c>Telemetry:Query:TraceHintMarginMinutes</c>
    /// (default 1) either side, instead of probing everything. A hint that finds nothing falls back to the unbounded read. A span that
    /// arrives after the list was read and lies beyond the margin is missing from a hinted read, which is the limit of a hint; an
    /// unhinted read is always whole. Every other provider ignores the hint.
    /// </summary>
    Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex, TraceTimeHint? hint, CancellationToken cancellationToken = default);

    Task<SpanModel?> GetSpanByIdAsync(string traceIdHex, string spanIdHex, CancellationToken cancellationToken = default);
    Task<List<SpanModel>> GetSpansByParentAsync(string traceIdHex, string parentSpanIdHex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Chart/stat-card summary for the traces list page (list-pages-server-side plan, Phase 3):
    /// volume/error/duration-percentile buckets, per-service RED stats, the latency heatmap and
    /// <c>listTotal</c>/<c>requestCount</c>, all derived from each trace's anchor -- its earliest
    /// span in scope (schema-simplification decision 10). Always the raw path: there are no rollup
    /// tables since schema 3.0.0, so a 3d/7d window on a busy tenant may come back as a lower bound.
    /// </summary>
    Task<TraceSummaryResult> GetTraceSummaryAsync(TraceSummaryQuery query, CancellationToken cancellationToken = default);

    /// <summary>Keyset-paged trace rows for the traces list page (decision 1), anchored on each trace's earliest span in scope (decision 10), pinned on <see cref="TraceQuery.AsOf"/> (decision 3).</summary>
    Task<TracePageResult> GetTracePageAsync(TraceQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The dashboard's Recent Errors/Slowest Traces widgets — newest errors or the slowest anchors, over the unfiltered
    /// population. Bounded by <c>SummaryTimeoutSeconds</c> like the summaries (it derives anchors over the whole
    /// window); on timeout the result is empty and flagged <see cref="TraceSamplesResult.TimedOut"/>.
    /// </summary>
    Task<TraceSamplesResult> GetTraceSamplesAsync(TraceSamplesQuery query, CancellationToken cancellationToken = default);

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
