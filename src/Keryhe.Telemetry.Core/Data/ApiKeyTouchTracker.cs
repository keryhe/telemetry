using System.Collections.Concurrent;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Process-lifetime set of API key hashes touched (successfully resolved, from cache or fresh)
/// since the last <see cref="Drain"/>. <see cref="CachingTenantResolver"/> marks a hash touched on
/// every resolution that yields a valid tenant; <see cref="ApiKeyTouchWorker"/> drains and
/// bulk-writes <c>last_used_at</c> via <see cref="IApiKeyTouchStore"/> on a timer.
///
/// A key marked touched many times between drains still yields exactly one entry in
/// <see cref="Drain"/> — that is the "at most one row per key per interval" the write path is
/// meant to guarantee, and it falls out for free from using a set rather than a counter.
///
/// <see cref="Drain"/> swaps the backing set atomically via <see cref="Interlocked.Exchange"/>, so
/// a <see cref="MarkTouched"/> call that read the OLD set reference immediately before a concurrent
/// drain can still write into it after the swap, losing that one touch for this interval. Given the
/// field this drives is already best-effort ("approximately when was this key last used"), that is
/// an acceptable trade for a lock-free hot path — the key gets marked again on its next use.
/// </summary>
public sealed class ApiKeyTouchTracker
{
    private ConcurrentDictionary<string, byte> _touched = new(StringComparer.Ordinal);

    public void MarkTouched(string keyHash) => _touched.TryAdd(keyHash, 0);

    /// <summary>Atomically swaps out the current set and returns what had accumulated since the last drain.</summary>
    public IReadOnlyCollection<string> Drain()
        => Interlocked.Exchange(ref _touched, new ConcurrentDictionary<string, byte>(StringComparer.Ordinal)).Keys.ToList();
}
