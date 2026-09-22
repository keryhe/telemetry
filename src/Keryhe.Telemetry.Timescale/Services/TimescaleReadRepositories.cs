using Npgsql;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.PostgreSQL.Services;

namespace Keryhe.Telemetry.Timescale.Services;

// =============================================================================
// TimescaleDB read repositories.
//
// For these four interfaces the SQL is identical to plain PostgreSQL (same logical
// table/column set), so the Timescale variants inherit the PostgreSQL reference
// implementations unchanged. Methods that would benefit from Timescale-only features
// (e.g. the log_severity_stats_daily continuous aggregate or time_bucket rollups) are
// not part of these interfaces; when such a method is added it is overridden here only.
// =============================================================================

public sealed class TimescaleTraceReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext, TraceQueryCache traceQueryCache)
    : PostgreSqlTraceReadRepository(dataSource, tenantContext, traceQueryCache);

public sealed class TimescaleMetricReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext, IConfiguration configuration)
    : PostgreSqlMetricReadRepository(dataSource, tenantContext, configuration);

public sealed class TimescaleLogReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : PostgreSqlLogReadRepository(dataSource, tenantContext);

public sealed class TimescaleResourceReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : PostgreSqlResourceReadRepository(dataSource, tenantContext);

public sealed class TimescaleAlertRuleRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : PostgreSqlAlertRuleRepository(dataSource, tenantContext);

public sealed class TimescaleTenantCatalogRepository(NpgsqlDataSource dataSource)
    : PostgreSqlTenantCatalogRepository(dataSource);

/// <summary>
/// Timescale retention sweeps reuse the plain-Postgres DML unchanged (same batching, same
/// indexes). These are on-demand supplements here, not the primary mechanism, until the schema
/// revision that removes Timescale's native <c>add_retention_policy</c> jobs for
/// <c>log_records</c> and the metric data-point tables ships — <c>spans</c> has neither a policy
/// nor hypertable status, so this is already the only trace retention that exists.
/// </summary>
public sealed class TimescaleRetentionSettingsRepository(NpgsqlDataSource dataSource)
    : PostgreSqlRetentionSettingsRepository(dataSource);
