using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// LOG REPOSITORY INTERFACE
// =============================================================================

public interface ILogRepository
{
    // Store operations
    Task<long> StoreLogRecordAsync(LogRecordModel logRecord, CancellationToken cancellationToken = default);
    Task<IEnumerable<long>> StoreLogRecordsBatchAsync(IEnumerable<LogRecordModel> logRecords, CancellationToken cancellationToken = default);
    
    // Retrieve operations
    Task<LogRecordModel?> GetLogRecordByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<IEnumerable<LogRecordModel>> GetLogRecordsByTraceIdAsync(string traceIdHex, CancellationToken cancellationToken = default);
    Task<IEnumerable<LogRecordModel>> GetLogRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
    Task<IEnumerable<LogRecordModel>> GetLogRecordsBySeverityAsync(int minSeverity, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);
   
    // Delete operations
    Task<bool> DeleteLogRecordAsync(long id, CancellationToken cancellationToken = default);
    Task<int> DeleteLogRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
}