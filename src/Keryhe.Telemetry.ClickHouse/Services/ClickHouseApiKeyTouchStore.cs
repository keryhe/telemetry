using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// Deliberate no-op implementation of <see cref="IApiKeyTouchStore"/>. An <c>ALTER TABLE ...
/// UPDATE</c> mutation is still disproportionate to what an approximate "last used" timestamp is
/// worth on ClickHouse's control-plane table, even amortized across a whole flush interval instead
/// of issued per gRPC request — mutations there are asynchronous, queue up, and would contend with
/// the retention and alert-rule mutations <c>ClickHouseWriteStore</c> and the alerting subsystem
/// already issue. <c>last_used_at</c> stays whatever it was set to at key creation unless
/// maintained out-of-band.
///
/// This used to be ClickHouse's own special case inside its <c>ITenantResolver</c>. Now every
/// provider bumps <c>last_used_at</c> at most once per flush interval instead of once per request,
/// so this is simply the same opt-out expressed as the interface it was always describing.
/// </summary>
public sealed class ClickHouseApiKeyTouchStore : IApiKeyTouchStore
{
    public Task TouchAsync(IReadOnlyCollection<string> keyHashes, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
