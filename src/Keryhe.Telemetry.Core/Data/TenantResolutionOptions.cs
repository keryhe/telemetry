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
}
