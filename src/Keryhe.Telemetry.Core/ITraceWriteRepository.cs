using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// TRACE WRITE REPOSITORY INTERFACE
// =============================================================================

public interface ITraceWriteRepository
{
    // Store operations
    Task<string> StoreTraceAsync(TraceModel trace, CancellationToken cancellationToken = default);
    Task<long> StoreSpanAsync(SpanModel span, CancellationToken cancellationToken = default);
    Task<IEnumerable<string>> StoreTracesBatchAsync(IEnumerable<TraceModel> traces, CancellationToken cancellationToken = default);
    Task<IEnumerable<long>> StoreSpansBatchAsync(IEnumerable<SpanModel> spans, CancellationToken cancellationToken = default);

    // Retention. See ITelemetryWriteStore for why this is the only delete offered and why it
    // is deliberately not tenant-scoped.
    Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
}
