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
        const string sql = "SELECT DISTINCT attributes_json FROM resources WHERE tenant_id = @tenantId";

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            sql, new { tenantId = TenantId }, cancellationToken: cancellationToken));

        return rows
            .Select(json => ExtractServiceName(DeserializeAttributes(json)))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
    }
}
