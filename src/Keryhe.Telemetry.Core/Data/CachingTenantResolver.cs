using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// The <see cref="ITenantResolver"/> every gRPC service actually resolves. Wraps whichever
/// provider's <see cref="IApiKeyLookup"/> is registered with a short-TTL <see cref="IMemoryCache"/>
/// and records successful resolutions on <see cref="ApiKeyTouchTracker"/> instead of writing
/// <c>last_used_at</c> inline.
///
/// Before this existed, tenant resolution cost a <c>SELECT</c> and an <c>UPDATE</c> — both against
/// the single row every agent of one tenant shares — on every single gRPC export. At real traffic
/// that serialized ingestion behind one row's lock on the relational providers. This collapses that
/// to roughly one <see cref="IApiKeyLookup"/> call per key per <see cref="TenantResolutionOptions.PositiveCacheTtlSeconds"/>
/// and zero <c>UPDATE</c>s on the request path at all — see <see cref="ApiKeyTouchWorker"/>.
///
/// Negative results (invalid or inactive key) are cached too, at a shorter TTL — see
/// <see cref="TenantResolutionOptions.NegativeCacheTtlSeconds"/> — so a bad key being retried does
/// not bypass the cache and hammer the database the way caching only positive results would.
/// </summary>
public sealed class CachingTenantResolver(
    IApiKeyLookup lookup,
    IMemoryCache cache,
    ApiKeyTouchTracker touchTracker,
    IOptions<TenantResolutionOptions> options) : ITenantResolver
{
    private readonly TenantResolutionOptions _options = options.Value;

    public async Task<long> ResolveTenantIdAsync(string keyHash, CancellationToken cancellationToken)
    {
        var cacheKey = CacheKey(keyHash);

        if (cache.TryGetValue<long>(cacheKey, out var cachedTenantId))
        {
            if (cachedTenantId > 0) touchTracker.MarkTouched(keyHash);
            return cachedTenantId;
        }

        var tenantId = await lookup.LookupTenantIdAsync(keyHash, cancellationToken);

        var ttl = tenantId > 0
            ? TimeSpan.FromSeconds(_options.PositiveCacheTtlSeconds)
            : TimeSpan.FromSeconds(_options.NegativeCacheTtlSeconds);
        cache.Set(cacheKey, tenantId, ttl);

        if (tenantId > 0) touchTracker.MarkTouched(keyHash);
        return tenantId;
    }

    // Namespaced so this resolver's entries cannot collide with anything else that might one day
    // share the same IMemoryCache instance.
    private static string CacheKey(string keyHash) => $"tenant-resolution:{keyHash}";
}
