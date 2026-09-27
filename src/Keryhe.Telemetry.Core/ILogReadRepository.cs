using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// LOG READ REPOSITORY INTERFACE
// =============================================================================

public interface ILogReadRepository
{
    // Retrieve operations
    Task<LogRecordModel?> GetLogRecordByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<IEnumerable<LogRecordModel>> GetLogRecordsByTraceIdAsync(string traceIdHex, CancellationToken cancellationToken = default);
    Task<IEnumerable<LogRecordModel>> GetLogRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);

    /// <summary>
    /// The <paramref name="before"/> log records immediately preceding and <paramref name="after"/>
    /// immediately following the anchor timestamp (for the same service, when given), ignoring any
    /// active list filters — the "what happened around this line" context view. Returned in ascending
    /// time order, anchor included.
    /// </summary>
    Task<IEnumerable<LogRecordModel>> GetSurroundingLogRecordsAsync(long anchorTimeUnixNano, string? service, int before, int after, CancellationToken cancellationToken = default);

    /// <summary>
    /// Chart/stat-card summary for the logs list page (list-pages-server-side plan, Phase 2):
    /// per-severity-group bucket counts, the exact (or lower-bound, on timeout) total, and the "new
    /// since asOf" count. Reads the rollup tables when eligible (decision 37), the raw
    /// <c>log_records</c> group-by otherwise.
    /// </summary>
    Task<LogSummaryResult> GetLogSummaryAsync(LogSummaryQuery query, CancellationToken cancellationToken = default);

    /// <summary>Keyset-paged log rows for the logs list page (decision 1), pinned on <see cref="LogQuery.AsOf"/> (decision 3).</summary>
    Task<LogPageResult> GetLogPageAsync(LogQuery query, CancellationToken cancellationToken = default);

    /// <summary>Server-side attribute facets (decision 15) over the newest matching rows, sampled and labelled as such.</summary>
    Task<LogFacetsResult> GetLogFacetsAsync(LogFacetsQuery query, CancellationToken cancellationToken = default);
}
