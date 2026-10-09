namespace Keryhe.Telemetry.Core;

/// <summary>
/// Bulk <c>last_used_at</c> maintenance for <c>api_keys</c>. Driven by
/// <c>Keryhe.Telemetry.Core.Data.ApiKeyTouchWorker</c> on a periodic interval rather than per gRPC
/// request — see that worker and <c>Keryhe.Telemetry.Core.Data.CachingTenantResolver</c> /
/// <c>ApiKeyTouchTracker</c>. One round trip per flush regardless of how many distinct keys were
/// used during that interval, and at most one row write per key per interval no matter how many
/// times that key was actually used.
///
/// Implemented by the control-plane providers (PostgreSQL, SQL Server, MySQL); ClickHouse holds no
/// API keys.
/// </summary>
public interface IApiKeyTouchStore
{
    Task TouchAsync(IReadOnlyCollection<string> keyHashes, CancellationToken cancellationToken);
}
