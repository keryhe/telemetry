namespace Keryhe.Telemetry.Core;

public interface ITenantResolver
{
    Task<TenantResolution> ResolveAsync(string keyHash, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="keyHash"/> is valid and unexpired in the resolver's cache, so resolving it costs no lookup. The handler
    /// never throttles such a key: a valid client behind a NAT keeps working next to a misconfigured one.
    /// </summary>
    bool IsCached(string keyHash) => false;
}

/// <summary>Why a key was refused.</summary>
public enum ApiKeyFailure
{
    /// <summary>Not a failure: the key is valid.</summary>
    None = 0,
    /// <summary>No active key row matches.</summary>
    Invalid,
    /// <summary>The key row exists and is active but its <c>expires_at</c> has passed.</summary>
    Expired,
    /// <summary>The lookup itself failed (database unreachable); retryable, never cached.</summary>
    Unavailable,
}

/// <summary>The outcome of resolving an API key hash.</summary>
public readonly record struct TenantResolution(long TenantId, long ApiKeyId, ApiKeyFailure Failure)
{
    /// <summary>The lookup exception behind <see cref="ApiKeyFailure.Unavailable"/>, for logging.</summary>
    public Exception? Error { get; init; }

    public bool Succeeded => Failure == ApiKeyFailure.None && TenantId > 0;
    public static TenantResolution Fail(ApiKeyFailure failure) => new(0, 0, failure);
}
