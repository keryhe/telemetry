using Npgsql;
using Keryhe.Telemetry.Core;
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

public sealed class TimescaleTraceReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : PostgreSqlTraceReadRepository(dataSource, tenantContext);

public sealed class TimescaleMetricReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : PostgreSqlMetricReadRepository(dataSource, tenantContext);

public sealed class TimescaleLogReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : PostgreSqlLogReadRepository(dataSource, tenantContext);

public sealed class TimescaleAlertRuleRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : PostgreSqlAlertRuleRepository(dataSource, tenantContext);

public sealed class TimescaleTenantCatalogRepository(NpgsqlDataSource dataSource)
    : PostgreSqlTenantCatalogRepository(dataSource);
