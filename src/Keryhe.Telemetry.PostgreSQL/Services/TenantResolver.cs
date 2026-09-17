using Npgsql;
using NpgsqlTypes;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.PostgreSQL.Services;

/// <summary>
/// PostgreSQL implementation of <see cref="IApiKeyLookup"/> — just the <c>SELECT</c> against
/// <c>api_keys</c>. Caching and <c>last_used_at</c> maintenance are handled once,
/// provider-agnostically, by <c>CachingTenantResolver</c> / <c>ApiKeyTouchWorker</c>; see
/// <see cref="IApiKeyLookup"/>.
/// </summary>
public class TenantResolver(NpgsqlDataSource dataSource) : IApiKeyLookup
{
    public async Task<long> LookupTenantIdAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        const string selectSql = """
            SELECT tenant_id
            FROM api_keys
            WHERE key_hash = $1
              AND is_active = TRUE
            LIMIT 1;
            """;

        await using var selectCmd = new NpgsqlCommand(selectSql, conn);
        selectCmd.Parameters.AddWithValue(NpgsqlDbType.Text, keyHash);
        var result = await selectCmd.ExecuteScalarAsync(cancellationToken);
        return result is long id ? id : 0L;
    }
}
