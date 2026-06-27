namespace Keryhe.Telemetry.Core;

// =============================================================================
// TELEMETRY WRITE STORE INTERFACE
// =============================================================================

/// <summary>
/// Provider-specific delete operations for the write path. The thin write
/// repositories (<see cref="ITraceWriteRepository"/> etc.) enqueue stores to the
/// ingestion channel directly, but delegate their <c>Delete*</c> operations here so
/// the actual DML is owned by the active provider rather than EF Core.
/// </summary>
public interface ITelemetryWriteStore
{
    // Traces
    Task<bool> DeleteTraceAsync(string traceIdHex, CancellationToken cancellationToken = default);
    Task<int> DeleteTracesByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
    Task<bool> DeleteSpanAsync(string traceIdHex, string spanIdHex, CancellationToken cancellationToken = default);

    // Metrics
    Task<bool> DeleteMetricAsync(long id, CancellationToken cancellationToken = default);
    Task<int> DeleteMetricsByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<int> DeleteMetricsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
    Task<int> DeleteOldMetricsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);

    // Logs
    Task<bool> DeleteLogRecordAsync(long id, CancellationToken cancellationToken = default);
    Task<int> DeleteLogRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
}
