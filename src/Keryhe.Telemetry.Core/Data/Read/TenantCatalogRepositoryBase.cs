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

    /// <summary>
    /// One <c>MAX(start_time)</c> per tenant: a correlated lookup that reads the newest entry of
    /// <c>(tenant_id, start_time_unix_nano)</c> for each tenant instead of grouping every span in the
    /// window (schema 3.0.0). ClickHouse, which does not support correlated subqueries, overrides it
    /// with a <c>GROUP BY tenant_id</c> that its sort key and partitions make cheap.
    /// </summary>
    protected virtual string TenantActivitySql => """
        SELECT TenantId, LastSeenUnixNano FROM (
            SELECT t.id AS TenantId,
                   (SELECT MAX(s.start_time_unix_nano) FROM spans s
                    WHERE s.tenant_id = t.id AND s.start_time_unix_nano >= @since) AS LastSeenUnixNano
            FROM tenants t
        ) activity
        WHERE LastSeenUnixNano IS NOT NULL
        """;

    public async Task<List<TenantActivity>> GetTenantActivityAsync(
        DateTime since, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<TenantActivityRow>(new CommandDefinition(
            TenantActivitySql,
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
