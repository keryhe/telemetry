using Microsoft.Extensions.DependencyInjection;
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
public class TenantResolver([FromKeyedServices(PostgreSqlControlPlane.ServiceKey)] NpgsqlDataSource dataSource) : IApiKeyLookup
{
    public async Task<ApiKeyLookupResult?> LookupAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        // expires_at is returned, not filtered on: CachingTenantResolver compares it on every resolution.
        const string selectSql = """
            SELECT id, tenant_id, expires_at
            FROM api_keys
            WHERE key_hash = $1
              AND is_active = TRUE
            LIMIT 1;
            """;

        await using var selectCmd = new NpgsqlCommand(selectSql, conn);
        selectCmd.Parameters.AddWithValue(NpgsqlDbType.Text, keyHash);
        await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        DateTimeOffset? expires = reader.IsDBNull(2) ? null : new DateTimeOffset(reader.GetFieldValue<DateTime>(2), TimeSpan.Zero);
        return new ApiKeyLookupResult(reader.GetInt64(1), reader.GetInt64(0), expires);
    }
}
