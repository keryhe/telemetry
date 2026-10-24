using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Sqlite.Services;

/// <summary>
/// SQLite implementation of <see cref="IApiKeyLookup"/>: just the <c>SELECT</c> against <c>api_keys</c>. Caching and
/// <c>last_used_at</c> maintenance are handled once, provider-agnostically, by <c>CachingTenantResolver</c> /
/// <c>ApiKeyTouchWorker</c>; see <see cref="IApiKeyLookup"/>.
/// </summary>
public sealed class SqliteApiKeyLookup(SqliteControlPlaneConnection controlPlane) : IApiKeyLookup
{
    public async Task<ApiKeyLookupResult?> LookupAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = await controlPlane.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        // expires_at (UTC text) is returned, not filtered on.
        cmd.CommandText = """
            SELECT id, tenant_id, expires_at
            FROM api_keys
            WHERE key_hash = @keyHash
              AND is_active = 1
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@keyHash", keyHash);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ApiKeyLookupResult(reader.GetInt64(1), reader.GetInt64(0),
            reader.IsDBNull(2) ? null : new DateTimeOffset(SqliteTime.FromColumn(reader.GetString(2))));
    }
}
