using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

/// <summary>
/// Reads the summary rollups (plans/summary-rollups.md) for the active tenant: at most buckets x
/// services rows however much traffic the window held. Both reads run under
/// <c>Telemetry:Query:SummaryTimeoutSeconds</c> and report a timeout instead of throwing.
/// </summary>
public interface IRollupReadRepository
{
    Task<RollupReadResult<RequestRollupAggregate>> GetRequestRollupAsync(RollupQuery query, CancellationToken cancellationToken = default);

    Task<RollupReadResult<LogRollupAggregate>> GetLogRollupAsync(RollupQuery query, CancellationToken cancellationToken = default);
}
