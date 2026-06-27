using System.Data.Common;
using Dapper;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="ITenantCatalogRepository"/>. Reads all tenants
/// (no tenant-scoping — this is a cross-tenant listing used for the tenant picker UI).
/// The SQL is dialect-neutral; providers supply only the connection.
/// </summary>
public abstract class TenantCatalogRepositoryBase : ITenantCatalogRepository
{
    protected abstract Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);

    public async Task<List<TenantInfo>> GetAllTenantsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<TenantRow>(new CommandDefinition(
            "SELECT id, name FROM tenants ORDER BY name",
            cancellationToken: cancellationToken));
        return rows.Select(r => new TenantInfo(r.Id, r.Name)).ToList();
    }

    private sealed class TenantRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
    }
}
