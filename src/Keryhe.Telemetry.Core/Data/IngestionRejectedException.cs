namespace Keryhe.Telemetry.Core.Data;

/// <summary>Why an export was refused before anything was enqueued.</summary>
public static class RefusalReasons
{
    /// <summary>The ingestion queue stayed full for the whole bounded wait.</summary>
    public const string Throttled = "throttled";
    /// <summary>The collector is draining for shutdown and no longer accepts exports.</summary>
    public const string ShuttingDown = "shutting_down";
    /// <summary>The request was malformed (a permanent, per-request problem).</summary>
    public const string Invalid = "invalid";
    /// <summary>The tenant already holds its share of the queue (<c>Telemetry:Ingestion:TenantQuota:MaxShare</c>).</summary>
    public const string TenantQuota = "tenant_quota";
    /// <summary>The tenant sent more records per second than its rate limit.</summary>
    public const string TenantRate = "tenant_rate";
}

/// <summary>
/// Thrown by the write repositories (and the channel's saturation pre-check) when an export cannot be accepted
/// right now. Nothing was enqueued, so nothing is acknowledged that is not stored; the transport adapters map it to
/// a retryable status carrying <see cref="RetryAfter"/> (gRPC <c>UNAVAILABLE</c> + <c>RetryInfo</c>, HTTP 503 +
/// <c>Retry-After</c>).
/// </summary>
public sealed class IngestionRejectedException(string signal, string reason, TimeSpan retryAfter)
    : Exception($"The {signal} ingestion queue is not accepting data ({reason}); retry in {retryAfter.TotalSeconds:0.#}s")
{
    public string Signal { get; } = signal;
    public string Reason { get; } = reason;
    public TimeSpan RetryAfter { get; } = retryAfter;
}
