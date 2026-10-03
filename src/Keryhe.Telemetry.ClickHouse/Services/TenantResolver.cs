using ClickHouse.Client.ADO;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// ClickHouse implementation of <see cref="IApiKeyLookup"/> — just the <c>SELECT</c> against
/// <c>api_keys</c>. Caching and <c>last_used_at</c> maintenance are handled once,
/// provider-agnostically, by <c>CachingTenantResolver</c> / <c>ApiKeyTouchWorker</c>; see
/// <see cref="IApiKeyLookup"/>. ClickHouse's <see cref="IApiKeyTouchStore"/>
/// (<see cref="ClickHouseApiKeyTouchStore"/>) is a no-op — see that type for why.
/// </summary>
public class TenantResolver(IConfiguration configuration) : IApiKeyLookup
{
    private readonly string _connectionString = configuration.GetConnectionString("Collector")!;

    public async Task<ApiKeyLookupResult?> LookupAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // FINAL: the table is tiny, and without it a replaced or mutated row (a revoke, an expiry) can
        // stay visible until a merge, which would break the revocation-latency promise.
        var row = await conn.QueryFirstOrDefaultAsync<KeyRow>(new CommandDefinition(
            "SELECT id AS Id, tenant_id AS TenantId, expires_at AS ExpiresAt FROM api_keys FINAL WHERE key_hash = @keyHash AND is_active = 1 LIMIT 1",
            new { keyHash }, cancellationToken: cancellationToken));

        if (row is not { TenantId: > 0 } r) return null;
        DateTimeOffset? expires = r.ExpiresAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null;
        return new ApiKeyLookupResult(r.TenantId, r.Id, expires);
    }

    private sealed class KeyRow
    {
        public long Id { get; set; }
        public long TenantId { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }
}
