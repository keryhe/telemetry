using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// <see cref="IResourceReadRepository"/>: the services a tenant has sent. There is no resources table in the row model, so the
/// services come from the small derived tables, which together cover every signal: <c>trace_index</c> (every service with spans),
/// the log rollup and the metric catalog (README D9).
/// </summary>
public sealed class ParquetResourceReadRepository(ITenantContext tenantContext, ParquetQueryEngine engine) : IResourceReadRepository
{
    public async Task<List<string>> GetDistinctServicesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.GetRequiredTenantId();
        await using var conn = await engine.OpenAsync(
        [
            new ParquetViewScope(ParquetTables.TraceIndexTable, tenant, null, null),
            new ParquetViewScope(ParquetTables.LogRollupTable, tenant, null, null),
            new ParquetViewScope(ParquetTables.MetricCatalogTable, tenant, null, null)
        ], cancellationToken);
        var rows = await Dapper.SqlMapper.QueryAsync<string>(conn, new Dapper.CommandDefinition("""
            SELECT DISTINCT service_name FROM (
                SELECT service_name FROM trace_index WHERE tenant_id = @tenantId
                UNION ALL SELECT service_name FROM log_rollup_minute WHERE tenant_id = @tenantId
                UNION ALL SELECT service_name FROM metric_catalog WHERE tenant_id = @tenantId
            ) WHERE service_name != '' ORDER BY service_name
            """, new { tenantId = tenant }, cancellationToken: cancellationToken));
        return rows.ToList();
    }
}
