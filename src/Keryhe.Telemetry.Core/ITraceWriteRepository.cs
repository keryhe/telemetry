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
    /// <param name="requestBytes">The protobuf size of the export these traces came from, reserved on the queue's byte budget (0 = not measured).</param>
    Task<IEnumerable<string>> StoreTracesBatchAsync(IEnumerable<TraceModel> traces, CancellationToken cancellationToken = default, long requestBytes = 0);
    Task<IEnumerable<long>> StoreSpansBatchAsync(IEnumerable<SpanModel> spans, CancellationToken cancellationToken = default);
}
