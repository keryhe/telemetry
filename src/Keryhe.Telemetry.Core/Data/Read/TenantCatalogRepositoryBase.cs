using System.Data.Common;
using Dapper;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Core.Data.Read;

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

    public async Task<List<TenantActivity>> GetTenantActivityAsync(
        DateTime since, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<TenantActivityRow>(new CommandDefinition(
            """
            SELECT r.tenant_id AS TenantId, MAX(s.start_time_unix_nano) AS LastSeenUnixNano
            FROM spans s
            INNER JOIN resources r ON s.resource_id = r.id
            WHERE s.start_time_unix_nano >= @since
            GROUP BY r.tenant_id
            """,
            new { since = TimeConversion.DateTimeToUnixNano(since) },
            cancellationToken: cancellationToken));

        return rows
            .Select(r => new TenantActivity(r.TenantId, TimeConversion.UnixNanoToDateTime(r.LastSeenUnixNano)))
            .ToList();
    }

    private sealed class TenantRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
    }

    private sealed class TenantActivityRow
    {
        public long TenantId { get; set; }
        public long LastSeenUnixNano { get; set; }
    }
}
