using System.Collections.Concurrent;

namespace Keryhe.Telemetry.Data;

public sealed class ResourceScopeCache
{
    private readonly ConcurrentDictionary<string, long> _resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _scopes = new(StringComparer.Ordinal);

    public bool TryGetResource(string hash, out long id) => _resources.TryGetValue(hash, out id);
    public void SetResource(string hash, long id) => _resources.TryAdd(hash, id);

    public bool TryGetScope(string hash, out long id) => _scopes.TryGetValue(hash, out id);
    public void SetScope(string hash, long id) => _scopes.TryAdd(hash, id);
}
