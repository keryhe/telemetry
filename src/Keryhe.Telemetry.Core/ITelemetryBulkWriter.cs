using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// TELEMETRY BULK WRITER INTERFACE
// =============================================================================

/// <summary>
/// Provider-specific bulk-flush contract driven by the generic ingestion worker.
/// The worker drains the bounded ingestion channels and hands each batch to the
/// matching flush method; each provider supplies the dialect-specific bulk insert
/// (SqlBulkCopy / MERGE for SqlServer, Npgsql binary COPY / ON CONFLICT for Postgres).
/// </summary>
public interface ITelemetryBulkWriter
{
    Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken cancellationToken = default);
    Task FlushTracesAsync(List<TraceModel> traces, CancellationToken cancellationToken = default);
    Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken cancellationToken = default);
}
