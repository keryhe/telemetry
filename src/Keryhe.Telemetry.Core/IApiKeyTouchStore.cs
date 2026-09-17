namespace Keryhe.Telemetry.Core;

/// <summary>
/// Bulk <c>last_used_at</c> maintenance for <c>api_keys</c>. Driven by
/// <c>Keryhe.Telemetry.Core.Data.ApiKeyTouchWorker</c> on a periodic interval rather than per gRPC
/// request — see that worker and <c>Keryhe.Telemetry.Core.Data.CachingTenantResolver</c> /
/// <c>ApiKeyTouchTracker</c>. One round trip per flush regardless of how many distinct keys were
/// used during that interval, and at most one row write per key per interval no matter how many
/// times that key was actually used.
///
/// ClickHouse's implementation is a deliberate no-op. Before this interface existed, ClickHouse
/// was the one provider that skipped <c>last_used_at</c> entirely, because a mutation per gRPC
/// request was disproportionate to the value of the field. Now every provider bumps it at most
/// once per flush interval instead of once per request — ClickHouse's own mutation cost is still
/// disproportionate even amortized across a whole interval, so it keeps opting out; the other four
/// providers get the field for the first time.
/// </summary>
public interface IApiKeyTouchStore
{
    Task TouchAsync(IReadOnlyCollection<string> keyHashes, CancellationToken cancellationToken);
}
