using System.Data.Common;
using Dapper;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;

namespace Keryhe.Telemetry.PostgreSQL.Services;

// =============================================================================
// PostgreSQL (plain) read repositories — the reference Dapper implementations.
// Connection comes from the host-configured NpgsqlDataSource (read connection string).
// =============================================================================

public class PostgreSqlTraceReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext, IConfiguration configuration)
    : TraceReadRepositoryBase(tenantContext, configuration)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);

    // Npgsql binds a list parameter as one native array, so match with = ANY(@ids) rather than
    // expanding an IN list (which Dapper only does for providers without array parameters).
    protected override string IdInPredicate(string column, string paramPrefix, IReadOnlyList<string> ids, int length, DynamicParameters parameters)
    {
        parameters.Add(paramPrefix, ids.ToArray());
        return $"{column} = ANY(@{paramPrefix})";
    }
}

public class PostgreSqlMetricReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext, IConfiguration configuration)
    : MetricReadRepositoryBase(tenantContext, configuration)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

public class PostgreSqlLogReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext, IConfiguration configuration)
    : LogReadRepositoryBase(tenantContext, configuration)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

public class PostgreSqlResourceReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : ResourceReadRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

public class PostgreSqlTenantCatalogRepository(NpgsqlDataSource dataSource)
    : TenantCatalogRepositoryBase
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

public class PostgreSqlAlertRuleRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : AlertRuleRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);

    protected override string JsonParam(string parameterName) => $"CAST(@{parameterName} AS jsonb)";

    protected override string ReturningIdentity => "RETURNING id";

    protected override string ClaimFireSql => """
        UPDATE alert_rules
        SET last_fired_at = NOW()
        WHERE id = @ruleId
          AND tenant_id = @tenantId
          AND enabled = TRUE
          AND (last_fired_at IS NULL
               OR last_fired_at < NOW() - (@cooldownMinutes * INTERVAL '1 minute'))
        """;
}

/// <summary>
/// PostgreSQL (plain) implementation of the <see cref="IRetentionSettingsRepository"/> sweeps: the
/// shared batched, per-tenant shape from <see cref="RetentionSettingsRepositoryBase"/>, in
/// PostgreSQL's dialect.
///
/// Postgres has neither <c>DELETE TOP (n)</c> nor <c>DELETE ... LIMIT n</c>, and the hot tables have
/// no primary key (schema 3.0.0), so a batch is bounded by selecting a capped set of <c>ctid</c>s
/// first and deleting those through a TID scan. The inner SELECT is the indexed access path --
/// <c>idx_spans_tenant_time</c>/<c>idx_log_tenant_time_id</c> for spans and logs, the BRIN index for
/// the data-point tables -- and each statement commits on its own, so a large first sweep never
/// holds one transaction open long enough to block autovacuum database-wide.
///
/// <c>ctid</c> is only unique within a single table, which is exactly why Timescale (whose
/// hypertables are many chunk tables) does not reuse this: it drops chunks instead.
/// </summary>
public class PostgreSqlRetentionSettingsRepository(NpgsqlDataSource dataSource)
    : RetentionSettingsRepositoryBase
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);

    protected override string BatchedDeleteSql(string table, string predicate) => $"""
        DELETE FROM {table}
        WHERE ctid = ANY (ARRAY(SELECT ctid FROM {table} WHERE {predicate} LIMIT {DeleteBatchSize}))
        """;
}

public class PostgreSqlRollupReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext, IConfiguration configuration)
    : RollupReadRepositoryBase(tenantContext, configuration)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}
