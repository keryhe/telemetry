using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// LOG WRITE REPOSITORY INTERFACE
// =============================================================================

public interface ILogWriteRepository
{
    // Store operations
    Task<long> StoreLogRecordAsync(LogRecordModel logRecord, CancellationToken cancellationToken = default);
    /// <param name="requestBytes">The protobuf size of the export these records came from, reserved on the queue's byte budget (0 = not measured).</param>
    Task<IEnumerable<long>> StoreLogRecordsBatchAsync(IEnumerable<LogRecordModel> logRecords, CancellationToken cancellationToken = default, long requestBytes = 0);
}
