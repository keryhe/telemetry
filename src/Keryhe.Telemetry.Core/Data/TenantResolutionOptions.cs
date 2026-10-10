namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Options for <see cref="CachingTenantResolver"/> and <see cref="ApiKeyTouchWorker"/>. Bound from
/// the <c>Telemetry:TenantResolution</c> configuration section.
/// </summary>
public sealed class TenantResolutionOptions
{
    /// <summary>Configuration section name these options bind from.</summary>
    public const string SectionName = "Telemetry:TenantResolution";

    /// <summary>
    /// Seconds a successful lookup (a valid, active key) is cached before the next request forces
    /// a fresh <see cref="IApiKeyLookup"/> call. Defaults to 30.
    /// </summary>
    public int PositiveCacheTtlSeconds { get; set; } = 30;

    /// <summary>
    /// Seconds an unsuccessful lookup (invalid or inactive key) is cached. Shorter than
    /// <see cref="PositiveCacheTtlSeconds"/> by default so a freshly-issued or just-reactivated key
    /// becomes usable quickly, while a bad key retried in a tight loop still mostly hits the cache
    /// rather than the database. Defaults to 5.
    /// </summary>
    public int NegativeCacheTtlSeconds { get; set; } = 5;

    /// <summary>Seconds between <c>api_keys.last_used_at</c> flush cycles. Defaults to 60.</summary>
    public int LastUsedFlushIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// The most control-plane key lookups in flight at once on one collector (lookups of different uncached keys; lookups of the same key
    /// are coalesced into one). A flood of requests with different random keys misses the cache every time, and each miss is a database
    /// query. Defaults to 16.
    /// </summary>
    public int MaxConcurrentLookups { get; set; } = 16;

    /// <summary>
    /// How long a lookup waits for one of the <see cref="MaxConcurrentLookups"/> slots before the request is answered as "lookup unavailable"
    /// (gRPC <c>UNAVAILABLE</c>, retryable, never cached). Defaults to 1000 ms.
    /// </summary>
    public int LookupQueueTimeoutMilliseconds { get; set; } = 1_000;

    public void Validate()
    {
        if (MaxConcurrentLookups < 1) throw new InvalidOperationException($"{SectionName}:{nameof(MaxConcurrentLookups)} must be at least 1 (was {MaxConcurrentLookups}).");
        if (LookupQueueTimeoutMilliseconds < 0) throw new InvalidOperationException($"{SectionName}:{nameof(LookupQueueTimeoutMilliseconds)} must not be negative (was {LookupQueueTimeoutMilliseconds}).");
    }
}
