namespace Keryhe.Telemetry.Core;

/// <summary>
/// Raw, uncached lookup of the key row behind an API key, by its SHA-256 hash. Implemented once per
/// provider — exactly the <c>SELECT</c> against <c>api_keys</c>, nothing else.
///
/// Caching, expiry and <c>last_used_at</c> maintenance are deliberately NOT this interface's job:
/// they are handled once, provider-agnostically, by <c>Keryhe.Telemetry.Core.Data.CachingTenantResolver</c>
/// (the <see cref="ITenantResolver"/> the collector's authentication handler resolves), which wraps
/// whichever provider's <see cref="IApiKeyLookup"/> is registered.
///
/// Returns null when the key does not match an <em>active</em> row. Expiry is NOT filtered in SQL: the
/// row's <see cref="ApiKeyLookupResult.ExpiresAt"/> is returned so the resolver can compare it against
/// its <see cref="TimeProvider"/> on every resolution, cache hits included.
/// </summary>
public interface IApiKeyLookup
{
    Task<ApiKeyLookupResult?> LookupAsync(string keyHash, CancellationToken cancellationToken);
}

/// <summary>An active <c>api_keys</c> row: its id, owning tenant and optional expiry (UTC).</summary>
public sealed record ApiKeyLookupResult(long TenantId, long ApiKeyId, DateTimeOffset? ExpiresAt);
