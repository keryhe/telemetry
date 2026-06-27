using System.Data.Common;
using Npgsql;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data.Read;

namespace Keryhe.Telemetry.PostgreSQL.Services;

// =============================================================================
// PostgreSQL (plain) read repositories — the reference Dapper implementations.
// Connection comes from the host-configured NpgsqlDataSource (read connection string).
// =============================================================================

public class PostgreSqlTraceReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : TraceReadRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

public class PostgreSqlMetricReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : MetricReadRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

public class PostgreSqlLogReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : LogReadRepositoryBase(tenantContext)
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
