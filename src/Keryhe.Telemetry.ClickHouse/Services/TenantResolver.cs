using ClickHouse.Client.ADO;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// ClickHouse implementation of <see cref="ITenantResolver"/>. Hashes the ingestion
/// <c>Authorization</c> key against <c>api_keys</c>. Unlike the relational providers this
/// does not bump <c>last_used_at</c> on every call: that would be an <c>ALTER TABLE ... UPDATE</c>
/// mutation per gRPC request, which is prohibitively expensive on ClickHouse. The field is
/// left to be maintained out-of-band if needed.
/// </summary>
public class TenantResolver(IConfiguration configuration) : ITenantResolver
{
    private readonly string _connectionString = configuration.GetConnectionString("Write")!;

    public async Task<long> ResolveTenantIdAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var tenantId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT tenant_id FROM api_keys WHERE key_hash = @keyHash AND is_active = 1 LIMIT 1",
            new { keyHash }, cancellationToken: cancellationToken));

        return tenantId is > 0 ? tenantId.Value : 0;
    }
}
