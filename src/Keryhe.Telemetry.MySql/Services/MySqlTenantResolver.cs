using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.MySql.Services;

public class MySqlTenantResolver(IConfiguration configuration) : ITenantResolver
{
    private readonly string _connectionString = configuration.GetConnectionString("Write")!;

    public async Task<long> ResolveTenantIdAsync(string keyHash, CancellationToken cancellationToken)
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
        var tenantId = result is not null && result != DBNull.Value ? Convert.ToInt64(result) : 0L;

        if (tenantId <= 0)
            return 0;

        const string updateSql = """
            UPDATE api_keys
            SET last_used_at = UTC_TIMESTAMP(6)
            WHERE key_hash = @keyHash;
            """;

        await using var updateCmd = new MySqlCommand(updateSql, conn);
        updateCmd.Parameters.AddWithValue("@keyHash", keyHash);
        await updateCmd.ExecuteNonQueryAsync(cancellationToken);

        return tenantId;
    }
}
