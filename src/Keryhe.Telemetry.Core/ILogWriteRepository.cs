using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// LOG WRITE REPOSITORY INTERFACE
// =============================================================================

public interface ILogWriteRepository
{
    // Store operations
    Task<long> StoreLogRecordAsync(LogRecordModel logRecord, CancellationToken cancellationToken = default);
    Task<IEnumerable<long>> StoreLogRecordsBatchAsync(IEnumerable<LogRecordModel> logRecords, CancellationToken cancellationToken = default);

    // Delete operations
    Task<bool> DeleteLogRecordAsync(long id, CancellationToken cancellationToken = default);
    Task<int> DeleteLogRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
}
