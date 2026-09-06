using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.SqlServer.Services;

public class TenantResolver(IConfiguration configuration) : ITenantResolver
{
    private readonly string _connectionString = configuration.GetConnectionString("Write")!;

    public async Task<long> ResolveTenantIdAsync(string keyHash, CancellationToken cancellationToken)
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
        var tenantId = result is long id ? id : 0L;

        if (tenantId <= 0)
            return 0;

        const string updateSql = """
            UPDATE api_keys
            SET last_used_at = GETUTCDATE()
            WHERE key_hash = @keyHash;
            """;

        await using var updateCmd = new SqlCommand(updateSql, conn);
        updateCmd.Parameters.AddWithValue("@keyHash", keyHash);
        await updateCmd.ExecuteNonQueryAsync(cancellationToken);

        return tenantId;
    }
}
