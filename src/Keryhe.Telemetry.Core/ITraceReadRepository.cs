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
    /// (ClickHouse's sort key) reads only that range, widened by <c>Telemetry:Query:TraceHintMarginMinutes</c>
    /// (default 1) either side, instead of probing everything. A hint that finds nothing falls back to the unbounded read. A span that
    /// arrives after the list was read and lies beyond the margin is missing from a hinted read, which is the limit of a hint; an
    /// unhinted read is always whole. Every other provider ignores the hint.
    /// </summary>
    Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex, TraceTimeHint? hint, CancellationToken cancellationToken = default);

    Task<SpanModel?> GetSpanByIdAsync(string traceIdHex, string spanIdHex, CancellationToken cancellationToken = default);
    Task<List<SpanModel>> GetSpansByParentAsync(string traceIdHex, string parentSpanIdHex, CancellationToken cancellationToken = default);

    /// <summary>The newest (or oldest) <see cref="TraceQuery.Limit"/> traces matching the filters, one row per trace anchored on its earliest span in scope, with whether more matched.</summary>
    Task<TraceListResult> GetTraceListAsync(TraceQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The dashboard's Recent Errors/Slowest Traces widgets — newest errors or the slowest anchors, over the unfiltered
    /// population. Bounded by <c>SummaryTimeoutSeconds</c> like the summaries (it derives anchors over the whole
    /// window); on timeout the result is empty and flagged <see cref="TraceSamplesResult.TimedOut"/>.
    /// </summary>
    Task<TraceSamplesResult> GetTraceSamplesAsync(TraceSamplesQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exact count of inbound spans (kind SERVER/CONSUMER) that started in <c>[start, end)</c> and ran at least
    /// <paramref name="minDurationMs"/>, for the slow-request alert (plans/summary-rollups.md). Raw spans, bounded by
    /// <c>SummaryTimeoutSeconds</c>; on timeout <see cref="SlowRequestCount.TimedOut"/> is set and the count is 0.
    /// </summary>
    Task<SlowRequestCount> CountSlowInboundSpansAsync(DateTime start, DateTime end, string? service, double minDurationMs, CancellationToken cancellationToken = default);

    // Analysis operations
    Task<List<ServiceDependency>> GetServiceDependenciesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);
    Task<Dictionary<string, int>> GetOperationCountsAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);
    Task<Dictionary<string, double>> GetAverageLatenciesAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);

    /// <summary>Per-operation RED metrics (rate, error%, p50/p95/p99, avg) for a service over the window.</summary>
    Task<List<OperationStats>> GetOperationStatsAsync(string serviceName, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming export (list-pages-server-side plan, Phase 8, decision 17): one <see cref="TraceInfo"/>
    /// row per trace matching the same filters as <see cref="GetTraceListAsync"/>, with no row cap. Span-level export is out of scope. Memory
    /// stays bounded to one internal chunk at a time (see <c>TraceReadRepositoryBase</c>'s own doc
    /// comment on this method for why it chunks rather than issuing one unbuffered query), not the
    /// whole matching population.
    /// </summary>
    IAsyncEnumerable<TraceInfo> ExportTracesAsync(TraceExportQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Result of <see cref="ITraceReadRepository.CountSlowInboundSpansAsync"/>.</summary>
public readonly record struct SlowRequestCount(long Count, bool TimedOut);
