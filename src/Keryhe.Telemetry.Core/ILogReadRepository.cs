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
    /// since asOf" count, from a group-by over <c>log_records</c> (there are no rollup tables since
    /// schema 3.0.0).
    /// </summary>
    Task<LogSummaryResult> GetLogSummaryAsync(LogSummaryQuery query, CancellationToken cancellationToken = default);

    /// <summary>Keyset-paged log rows for the logs list page (decision 1), pinned on <see cref="LogQuery.AsOf"/> (decision 3).</summary>
    Task<LogPageResult> GetLogPageAsync(LogQuery query, CancellationToken cancellationToken = default);

    /// <summary>Server-side attribute facets (decision 15) over the newest matching rows, sampled and labelled as such.</summary>
    Task<LogFacetsResult> GetLogFacetsAsync(LogFacetsQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming export (list-pages-server-side plan, Phase 8, decision 17): every log record
    /// matching the same filters as <see cref="GetLogSummaryAsync"/>/<see cref="GetLogPageAsync"/>,
    /// with no row cap and no paging — the caller (the export controller) writes each record to the
    /// HTTP response as it arrives. Implementations read with Dapper's unbuffered async query mode
    /// so the whole matching set is never materialized in memory at once.
    /// </summary>
    IAsyncEnumerable<LogRecordModel> ExportLogsAsync(LogExportQuery query, CancellationToken cancellationToken = default);
}
