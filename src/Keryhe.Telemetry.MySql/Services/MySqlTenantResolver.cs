using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.MySql.Services;

/// <summary>
/// MySQL implementation of <see cref="IApiKeyLookup"/> — just the <c>SELECT</c> against
/// <c>api_keys</c>. Caching and <c>last_used_at</c> maintenance are handled once,
/// provider-agnostically, by <c>CachingTenantResolver</c> / <c>ApiKeyTouchWorker</c>; see
/// <see cref="IApiKeyLookup"/>.
/// </summary>
public class MySqlTenantResolver(IConfiguration configuration) : IApiKeyLookup
{
    private readonly string _connectionString = configuration.GetConnectionString("Collector")!;

    public async Task<long> LookupTenantIdAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string selectSql = """
            SELECT tenant_id
            FROM api_keys
            WHERE key_hash = @keyHash
              AND is_active = 1
            LIMIT 1;
            """;

        await using var selectCmd = new MySqlCommand(selectSql, conn);
        selectCmd.Parameters.AddWithValue("@keyHash", keyHash);
        var result = await selectCmd.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value ? Convert.ToInt64(result) : 0L;
    }
}
