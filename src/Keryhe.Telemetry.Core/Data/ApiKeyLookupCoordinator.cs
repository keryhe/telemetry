using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>The lookup could not start within <see cref="TenantResolutionOptions.LookupQueueTimeoutMilliseconds"/>.</summary>
public sealed class LookupQueueTimeoutException() : Exception("The control-plane key lookup queue is full.");

/// <summary>
/// Stands between <see cref="CachingTenantResolver"/> and the control-plane database for keys that are not cached.
/// Lookups of the same key hash are coalesced into one query (a cold start with many clients sharing one valid key would otherwise send
/// one query per concurrent request), and lookups of different keys run at most <see cref="TenantResolutionOptions.MaxConcurrentLookups"/>
/// at a time (a flood of different random keys misses the cache every time). A lookup that waits longer than
/// <see cref="TenantResolutionOptions.LookupQueueTimeoutMilliseconds"/> for a slot throws <see cref="LookupQueueTimeoutException"/>.
/// A singleton: the in-flight map and the slots are shared by every request, and the shared query runs in its own scope so no
/// one request's disposal can cut it off for the others waiting on it.
/// </summary>
public sealed class ApiKeyLookupCoordinator(IServiceScopeFactory scopes, IOptions<TenantResolutionOptions> options)
{
    private readonly TenantResolutionOptions _options = options.Value;
    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.Value.MaxConcurrentLookups));
    private readonly ConcurrentDictionary<string, Lazy<Task<ApiKeyLookupResult?>>> _inFlight = new();

    /// <summary>Lookups running or waiting for a slot right now (for tests and diagnostics).</summary>
    public int InFlight => _inFlight.Count;

    public Task<ApiKeyLookupResult?> LookupAsync(string keyHash, CancellationToken cancellationToken)
    {
        var shared = _inFlight.GetOrAdd(keyHash, h => new Lazy<Task<ApiKeyLookupResult?>>(() => RunAsync(h)));
        // Each caller may stop waiting on its own cancellation without cancelling the query the others share.
        return shared.Value.WaitAsync(cancellationToken);
    }

    private async Task<ApiKeyLookupResult?> RunAsync(string keyHash)
    {
        // Always completes after the entry was published, so the removal below cannot run before the add.
        await Task.Yield();
        try
        {
            if (!await _slots.WaitAsync(TimeSpan.FromMilliseconds(_options.LookupQueueTimeoutMilliseconds)))
                throw new LookupQueueTimeoutException();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<IApiKeyLookup>().LookupAsync(keyHash, CancellationToken.None);
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            _inFlight.TryRemove(keyHash, out _);
        }
    }
}
