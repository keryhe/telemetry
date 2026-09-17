namespace Keryhe.Telemetry.Core;

/// <summary>
/// Raw, uncached lookup of the tenant owning an API key, by its SHA-256 hash. Implemented once per
/// provider — exactly the <c>SELECT</c> against <c>api_keys</c>, nothing else.
///
/// Caching and <c>last_used_at</c> maintenance are deliberately NOT this interface's job: they are
/// handled once, provider-agnostically, by <c>Keryhe.Telemetry.Core.Data.CachingTenantResolver</c>
/// (the <see cref="ITenantResolver"/> every gRPC service actually resolves), which wraps whichever
/// provider's <see cref="IApiKeyLookup"/> is registered. Folding the SELECT into a cache and the
/// UPDATE into a batched background sweep once, here, is what lets every provider get the same
/// off-the-hot-path behavior without five separate reimplementations of it.
///
/// Returns 0 (never negative) when the key does not match an active row — the same contract
/// <see cref="ITenantResolver.ResolveTenantIdAsync"/> already exposes, which callers already treat
/// "tenantId &lt;= 0" as "unauthenticated."
/// </summary>
public interface IApiKeyLookup
{
    Task<long> LookupTenantIdAsync(string keyHash, CancellationToken cancellationToken);
}
