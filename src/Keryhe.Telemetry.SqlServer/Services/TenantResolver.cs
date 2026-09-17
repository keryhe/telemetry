using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.SqlServer.Services;

/// <summary>
/// SQL Server implementation of <see cref="IApiKeyLookup"/> — just the <c>SELECT</c> against
/// <c>api_keys</c>. Caching and <c>last_used_at</c> maintenance are handled once,
/// provider-agnostically, by <c>CachingTenantResolver</c> / <c>ApiKeyTouchWorker</c>; see
/// <see cref="IApiKeyLookup"/>.
/// </summary>
public class TenantResolver(IConfiguration configuration) : IApiKeyLookup
{
    private readonly string _connectionString = configuration.GetConnectionString("Write")!;

    public async Task<long> LookupTenantIdAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string selectSql = """
            SELECT TOP 1 tenant_id
            FROM api_keys
            WHERE key_hash = @keyHash
              AND is_active = 1;
            """;

        await using var selectCmd = new SqlCommand(selectSql, conn);
        selectCmd.Parameters.AddWithValue("@keyHash", keyHash);
        var result = await selectCmd.ExecuteScalarAsync(cancellationToken);
        return result is long id ? id : 0L;
    }
}
