using System.Data.Common;
using Dapper;

namespace Keryhe.Telemetry.Admin.Data;

/// <summary>
/// Dialect-neutral SQL shared by every supported provider. The only things a provider overrides
/// are the two inserts that need to return a generated id, connection opening, unique-violation
/// detection, and the connection-target description — see plans/admin-tui.md, section 5.2.
/// </summary>
public abstract class AdminRepositoryBase : IAdminRepository
{
    protected abstract Task<DbConnection> OpenConnectionAsync(CancellationToken ct);

    /// <summary>Must return the new row's id, e.g. via <c>RETURNING id</c> or <c>OUTPUT INSERTED.id</c>.</summary>
    protected abstract string InsertTenantSql { get; }

    /// <summary>Must return the new row's id, e.g. via <c>RETURNING id</c> or <c>OUTPUT INSERTED.id</c>.</summary>
    protected abstract string InsertApiKeySql { get; }

    /// <summary>True if <paramref name="ex"/> is this provider's unique-constraint violation.</summary>
    protected abstract bool IsUniqueViolation(DbException ex);

    public abstract (string Server, string Database) DescribeTarget();

    public abstract string CreatedAtColumnHeader { get; }

    public async Task CheckConnectivityAsync(CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TenantRow>> GetTenantsAsync(CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TenantQueryRow>(new CommandDefinition(
            """
            SELECT t.id AS Id, t.name AS Name, t.created_at AS CreatedAt,
                   COUNT(k.id) AS KeyCount,
                   COUNT(CASE WHEN k.is_active = @active THEN 1 END) AS ActiveKeyCount
            FROM tenants t
            LEFT JOIN api_keys k ON k.tenant_id = t.id
            GROUP BY t.id, t.name, t.created_at
            ORDER BY t.name
            """,
            new { active = true },
            cancellationToken: ct));

        return rows
            .Select(r => new TenantRow(r.Id, r.Name, r.CreatedAt, r.KeyCount, r.ActiveKeyCount))
            .ToList();
    }

    public async Task<long> CreateTenantAsync(string name, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        try
        {
            return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                InsertTenantSql, new { name }, cancellationToken: ct));
        }
        catch (DbException ex) when (IsUniqueViolation(ex))
        {
            throw new UniqueConstraintViolationException($"A tenant named '{name}' already exists.");
        }
    }

    public async Task<IReadOnlyList<ApiKeyRow>> GetApiKeysAsync(long tenantId, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ApiKeyQueryRow>(new CommandDefinition(
            """
            SELECT id AS Id, name AS Name, is_active AS IsActive, created_at AS CreatedAt,
                   last_used_at AS LastUsedAt, key_hash AS KeyHash
            FROM api_keys
            WHERE tenant_id = @tenantId
            ORDER BY created_at DESC
            """,
            new { tenantId },
            cancellationToken: ct));

        return rows
            .Select(r => new ApiKeyRow(r.Id, r.Name, r.IsActive, r.CreatedAt, r.LastUsedAt, r.KeyHash))
            .ToList();
    }

    public async Task<long> CreateApiKeyAsync(long tenantId, string name, string keyHash, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            InsertApiKeySql, new { tenantId, name, keyHash }, cancellationToken: ct));
    }

    public async Task<bool> SetApiKeyActiveAsync(long apiKeyId, bool active, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE api_keys SET is_active = @active WHERE id = @apiKeyId",
            new { active, apiKeyId },
            cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> DeleteApiKeyAsync(long apiKeyId, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM api_keys WHERE id = @apiKeyId",
            new { apiKeyId },
            cancellationToken: ct));
        return affected > 0;
    }

    private sealed class TenantQueryRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
        public DateTime CreatedAt { get; set; }
        public int KeyCount { get; set; }
        public int ActiveKeyCount { get; set; }
    }

    private sealed class ApiKeyQueryRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public string KeyHash { get; set; } = null!;
    }
}
