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
    Task<List<string>> GetDistinctServicesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);
}
