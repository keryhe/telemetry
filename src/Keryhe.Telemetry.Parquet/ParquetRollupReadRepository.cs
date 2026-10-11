using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// The summary-rollup reads (README D8, D9) over <c>request_rollup_minute</c> and <c>log_rollup_minute</c>: the shared SQL runs unchanged, the repository
/// supplying the DuckDB dialect (integer division <c>//</c>, <c>BIGINT</c> casts; <c>@name</c> parameters are rewritten by
/// <see cref="NamedParameterConnection"/>) and a connection whose view covers the window's hours.
/// </summary>
public sealed class ParquetRollupReadRepository(IConfiguration configuration, ITenantContext tenantContext, ParquetQueryEngine engine)
    : RollupReadRepositoryBase(tenantContext, configuration)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await engine.OpenAsync([], cancellationToken);

    protected override async Task<DbConnection> OpenRollupConnectionAsync(string kind, RollupQuery query, CancellationToken cancellationToken)
    {
        if (kind == "request")
            return await engine.OpenAsync([new ParquetViewScope(ParquetTables.RequestRollupTable, TenantId, query.StartNano, query.EndNano)], cancellationToken);

        return await engine.OpenAsync([new ParquetViewScope(ParquetTables.LogRollupTable, TenantId, query.StartNano, query.EndNano)], cancellationToken);
    }

    protected override string BucketIndexExpr(string numerator, string denominator) => $"({numerator} // {denominator})";
    protected override string BigintExpr(string expression) => $"CAST({expression} AS BIGINT)";
}
