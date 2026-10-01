using Dapper;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="IResourceReadRepository"/>. Reads <c>resources</c>
/// directly — no join through <c>spans</c>/<c>metrics</c>/<c>log_records</c> — so the result is
/// every service the tenant has ever sent, independent of signal type or time range.
/// </summary>
public abstract class ResourceReadRepositoryBase : DapperReadRepository, IResourceReadRepository
{
    protected ResourceReadRepositoryBase(ITenantContext tenantContext) : base(tenantContext) { }

    public async Task<List<string>> GetDistinctServicesAsync(CancellationToken cancellationToken = default)
    {
        // service_name is a column on resources (extracted from service.name at upsert), indexed with
        // the tenant, so this is a tenant-prefix scan of idx_resources_tenant_service.
        const string sql = "SELECT DISTINCT service_name FROM resources WHERE tenant_id = @tenantId AND service_name IS NOT NULL";

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            sql, new { tenantId = TenantId }, cancellationToken: cancellationToken));

        return rows
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .OrderBy(s => s)
            .ToList();
    }
}
