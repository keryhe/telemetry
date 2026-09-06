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

    // Retention. See ITelemetryWriteStore for why this is the only delete offered and why it
    // is deliberately not tenant-scoped.
    Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
}
