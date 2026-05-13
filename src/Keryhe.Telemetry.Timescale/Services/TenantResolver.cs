using Npgsql;
using NpgsqlTypes;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Timescale.Services;

public class TenantResolver(NpgsqlDataSource dataSource) : ITenantResolver
{
    public async Task<long> ResolveTenantIdAsync(string keyHash, CancellationToken cancellationToken)
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
        var tenantId = result is long id ? id : 0L;

        if (tenantId <= 0)
            return 0;

        const string updateSql = """
            UPDATE api_keys
            SET last_used_at = NOW()
            WHERE key_hash = $1;
            """;

        await using var updateCmd = new NpgsqlCommand(updateSql, conn);
        updateCmd.Parameters.AddWithValue(NpgsqlDbType.Text, keyHash);
        await updateCmd.ExecuteNonQueryAsync(cancellationToken);

        return tenantId;
    }
}
