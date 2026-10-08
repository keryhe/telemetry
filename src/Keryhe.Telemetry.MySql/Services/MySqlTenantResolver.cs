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
public class MySqlTenantResolver(ControlPlaneConnection controlPlane) : IApiKeyLookup
{
    private readonly string _connectionString = controlPlane.ConnectionString;

    public async Task<ApiKeyLookupResult?> LookupAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // expires_at (DATETIME(6), UTC by convention) is returned, not filtered on.
        const string selectSql = """
            SELECT id, tenant_id, expires_at
            FROM api_keys
            WHERE key_hash = @keyHash
              AND is_active = 1
            LIMIT 1;
            """;

        await using var selectCmd = new MySqlCommand(selectSql, conn);
        selectCmd.Parameters.AddWithValue("@keyHash", keyHash);
        await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ApiKeyLookupResult(reader.GetInt64(1), reader.GetInt64(0),
            reader.IsDBNull(2) ? null : AsUtc(reader.GetDateTime(2)));
    }

    /// <summary>A DATETIME(6) holds UTC by convention; the driver hands it back Unspecified, so stamp it UTC.</summary>
    internal static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
