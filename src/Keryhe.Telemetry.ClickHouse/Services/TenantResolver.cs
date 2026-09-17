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

    public async Task<long> LookupTenantIdAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var tenantId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT tenant_id FROM api_keys WHERE key_hash = @keyHash AND is_active = 1 LIMIT 1",
            new { keyHash }, cancellationToken: cancellationToken));

        return tenantId is > 0 ? tenantId.Value : 0;
    }
}
