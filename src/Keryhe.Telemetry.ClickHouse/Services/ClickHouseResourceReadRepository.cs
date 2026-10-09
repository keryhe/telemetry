using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Read;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// <see cref="IResourceReadRepository"/>: the services a tenant has sent. There is no resources table in the row model, so the
/// services come from the small derived tables, which together cover every signal: the request rollup (services with inbound
/// spans), the log rollup and the metric catalog. A service that has only ever sent non-inbound spans and nothing else is not listed.
/// </summary>
public sealed class ClickHouseResourceReadRepository : DapperReadRepository, IResourceReadRepository
{
    private readonly string _connectionString;

    public ClickHouseResourceReadRepository(IConfiguration configuration, ITenantContext tenantContext) : base(tenantContext)
        => _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    public async Task<List<string>> GetDistinctServicesAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT service_name FROM (
                SELECT service_name FROM request_rollup_minute WHERE tenant_id = @tenantId
                UNION ALL SELECT service_name FROM log_rollup_minute WHERE tenant_id = @tenantId
                UNION ALL SELECT service_name FROM metric_catalog WHERE tenant_id = @tenantId
            )
            """, new { tenantId = (ulong)TenantId }, cancellationToken: cancellationToken));
        return rows.Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}
