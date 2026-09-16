namespace Keryhe.Telemetry.Core;

public interface ITenantCatalogRepository
{
    Task<List<TenantInfo>> GetAllTenantsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Most recent span start time per tenant, across all tenants, for spans no older than
    /// <paramref name="since"/>. Tenants with nothing in that lookback are absent from the
    /// result rather than present with a null — callers treat "missing" as "no data".
    /// </summary>
    /// <param name="since">
    /// Lower bound on the scan. Required, not optional: an unbounded <c>MAX()</c> over
    /// <c>spans</c> is a full table scan, while a bounded one is a range scan on the
    /// <c>start_time_unix_nano</c> index.
    /// </param>
    Task<List<TenantActivity>> GetTenantActivityAsync(
        DateTime since, CancellationToken cancellationToken = default);
}

public sealed record TenantInfo(long Id, string Name);

/// <summary>When a tenant last sent telemetry, independent of any dashboard time window.</summary>
public sealed record TenantActivity(long TenantId, DateTime LastSeenUtc);
