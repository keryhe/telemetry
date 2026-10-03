using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// The <see cref="ITenantResolver"/> the collector's authentication handler resolves. Wraps whichever
/// provider's <see cref="IApiKeyLookup"/> is registered with a short-TTL <see cref="IMemoryCache"/>
/// and records successful resolutions on <see cref="ApiKeyTouchTracker"/> instead of writing
/// <c>last_used_at</c> inline.
///
/// The cache holds the <em>lookup result itself</em> (or its absence), not a bare tenant id, so a
/// negative-cached key keeps its reason and an expiring key can be re-checked on every call. Expiry is
/// compared against the injected <see cref="TimeProvider"/> on every resolution, cache hits included,
/// so a key stops exactly at its expiry regardless of the cache TTL.
///
/// Negative results (invalid, inactive or expired key) are cached at the shorter
/// <see cref="TenantResolutionOptions.NegativeCacheTtlSeconds"/> so a bad key retried in a loop does
/// not hammer the database. A lookup that throws is NOT cached: the failure is reported as
/// <see cref="ApiKeyFailure.Unavailable"/> and the next call tries again.
/// </summary>
public sealed class CachingTenantResolver(
    IApiKeyLookup lookup,
    IMemoryCache cache,
    ApiKeyTouchTracker touchTracker,
    IOptions<TenantResolutionOptions> options,
    TimeProvider timeProvider) : ITenantResolver
{
    private readonly TenantResolutionOptions _options = options.Value;

    // A cached "no active row" is a null result; wrapped so TryGetValue can tell it from a miss.
    private sealed record Entry(ApiKeyLookupResult? Result);

    public async Task<TenantResolution> ResolveAsync(string keyHash, CancellationToken cancellationToken)
    {
        var cacheKey = CacheKey(keyHash);

        if (!cache.TryGetValue<Entry>(cacheKey, out var entry) || entry is null)
        {
            ApiKeyLookupResult? result;
            try
            {
                result = await lookup.LookupAsync(keyHash, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Never cached: the next call tries the database again.
                return TenantResolution.Fail(ApiKeyFailure.Unavailable) with { Error = ex };
            }

            entry = new Entry(result);
            var ttl = result is not null && !IsExpired(result)
                ? TimeSpan.FromSeconds(_options.PositiveCacheTtlSeconds)
                : TimeSpan.FromSeconds(_options.NegativeCacheTtlSeconds);
            cache.Set(cacheKey, entry, ttl);
        }

        if (entry.Result is null) return TenantResolution.Fail(ApiKeyFailure.Invalid);
        if (IsExpired(entry.Result)) return TenantResolution.Fail(ApiKeyFailure.Expired);

        touchTracker.MarkTouched(keyHash);
        return new TenantResolution(entry.Result.TenantId, entry.Result.ApiKeyId, ApiKeyFailure.None);
    }

    private bool IsExpired(ApiKeyLookupResult r) => r.ExpiresAt is { } at && timeProvider.GetUtcNow() >= at;

    private static string CacheKey(string keyHash) => $"tenant-resolution:{keyHash}";
}
