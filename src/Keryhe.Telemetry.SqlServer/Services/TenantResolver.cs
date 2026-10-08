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
public class TenantResolver(ControlPlaneConnection controlPlane) : IApiKeyLookup
{
    private readonly string _connectionString = controlPlane.ConnectionString;

    public async Task<ApiKeyLookupResult?> LookupAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // expires_at (DATETIMEOFFSET) is returned, not filtered on: CachingTenantResolver compares it.
        const string selectSql = """
            SELECT TOP 1 id, tenant_id, expires_at
            FROM api_keys
            WHERE key_hash = @keyHash
              AND is_active = 1;
            """;

        await using var selectCmd = new SqlCommand(selectSql, conn);
        selectCmd.Parameters.AddWithValue("@keyHash", keyHash);
        await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        DateTimeOffset? expires = reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2);
        return new ApiKeyLookupResult(reader.GetInt64(1), reader.GetInt64(0), expires);
    }
}
