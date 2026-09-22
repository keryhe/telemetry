using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Options for <see cref="TraceQueryCache"/>. Bound from the <c>Telemetry:TraceQueryCache</c>
/// configuration section.
/// </summary>
public sealed class TraceQueryCacheOptions
{
    /// <summary>Configuration section name these options bind from.</summary>
    public const string SectionName = "Telemetry:TraceQueryCache";

    /// <summary>Seconds an entry survives before the next request forces a fresh scan. Defaults to 30.</summary>
    public int TtlSeconds { get; set; } = 30;

    /// <summary>
    /// Cache-wide size budget in trace rows (one entry's <c>Size</c> is its row count, since
    /// <see cref="MemoryCache"/> has no notion of bytes). Defaults to 200,000 — a handful of
    /// wide-window results, not "every window any tenant has queried recently".
    /// </summary>
    public long SizeLimit { get; set; } = 200_000;
}

/// <summary>
/// Short-TTL memo of <see cref="TraceReadRepositoryBase.ComputeTraceInfosAsync"/>'s per-tenant,
/// per-filter, per-window scan (list-page-scale plan, Phase 3). Paging past the traces list
/// page's overview cap, changing the sort column, and reloading the same filter set within the
/// TTL now resolve to the SAME already-materialized span scan instead of a fresh one — sort,
/// page and offset are deliberately excluded from the cache key (they're views of the same list,
/// applied by the caller after the lookup, not part of what the scan computed).
///
/// A dedicated <see cref="MemoryCache"/> instance rather than the app's shared
/// <see cref="IMemoryCache"/>: this feature's <see cref="TraceQueryCacheOptions.SizeLimit"/> and
/// eviction are independent of — and would otherwise force annotating every entry of — an
/// unrelated cache sharing that instance, like <see cref="CachingTenantResolver"/>'s
/// tenant-resolution entries (which have no <c>Size</c> set today and would throw the moment a
/// shared cache gained a <c>SizeLimit</c>).
///
/// 30s TTL vs. auto-refresh (list-page-scale plan §14.1): a preset auto-refresh tick calls the
/// Angular client's <c>TimeRangeService.setPreset</c>, which recomputes the window against the
/// current wall clock — the window itself slides forward every tick, so its start/end (part of
/// the cache key) essentially never repeats within the TTL, and auto-refresh keeps hitting the
/// database exactly as before. A frozen custom range can legitimately hit the cache on a manual
/// refresh within the TTL; that is accepted as "fresh enough" for an already-past window, the
/// same tradeoff <see cref="CachingTenantResolver"/> makes for tenant lookups.
/// </summary>
public sealed class TraceQueryCache : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _ttl;

    public TraceQueryCache(IOptions<TraceQueryCacheOptions> options)
    {
        var o = options.Value;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = o.SizeLimit });
        _ttl = TimeSpan.FromSeconds(o.TtlSeconds);
    }

    public bool TryGet<T>(string key, out List<T> value)
    {
        if (_cache.TryGetValue(key, out List<T>? cached) && cached != null)
        {
            value = cached;
            return true;
        }
        value = null!;
        return false;
    }

    public void Set<T>(string key, List<T> value)
        => _cache.Set(key, value, new MemoryCacheEntryOptions
        {
            Size = value.Count,
            AbsoluteExpirationRelativeToNow = _ttl,
        });

    public void Dispose() => _cache.Dispose();
}
