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

    // Delete operations
    Task<bool> DeleteTraceAsync(string traceIdHex, CancellationToken cancellationToken = default);
    Task<int> DeleteTracesByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
    Task<bool> DeleteSpanAsync(string traceIdHex, string spanIdHex, CancellationToken cancellationToken = default);
}
