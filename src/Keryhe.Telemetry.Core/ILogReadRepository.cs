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
    Task<IEnumerable<LogRecordModel>> GetLogRecordsBySeverityAsync(int minSeverity, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);

    /// <summary>Server-side filtered + paged log query; returns a page of rows plus the full filtered total.</summary>
    Task<PagedResult<LogRecordModel>> QueryLogRecordsAsync(LogQuery query, CancellationToken cancellationToken = default);

    /// <summary>True volume-by-severity histogram (fixed bucket count over the full filtered range) for the logs list/dashboard chart, unaffected by any row-count cap.</summary>
    Task<List<LogVolumeBucket>> GetLogHistogramAsync(HistogramQuery query, CancellationToken cancellationToken = default);
    Task<List<string>> GetDistinctServicesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The <paramref name="before"/> log records immediately preceding and <paramref name="after"/>
    /// immediately following the anchor timestamp (for the same service, when given), ignoring any
    /// active list filters — the "what happened around this line" context view. Returned in ascending
    /// time order, anchor included.
    /// </summary>
    Task<IEnumerable<LogRecordModel>> GetSurroundingLogRecordsAsync(long anchorTimeUnixNano, string? service, int before, int after, CancellationToken cancellationToken = default);
}
