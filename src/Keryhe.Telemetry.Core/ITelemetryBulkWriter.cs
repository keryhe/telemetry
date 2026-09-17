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

    /// <summary>
    /// Flushes a flat list of spans, not traces: <c>TraceWriteRepository</c> flattens the
    /// caller's <see cref="TraceModel"/> groupings into individual <see cref="SpanModel"/>s before
    /// they ever reach the ingestion channel, resolving each span's effective
    /// <see cref="SpanModel.Resource"/>/<see cref="SpanModel.InstrumentationScope"/> (falling back
    /// to its trace's, exactly as before) at that single point instead of leaving every provider to
    /// re-flatten the same grouping and re-apply the same fallback on every flush.
    /// </summary>
    Task FlushTracesAsync(List<SpanModel> spans, CancellationToken cancellationToken = default);

    Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken cancellationToken = default);
}
