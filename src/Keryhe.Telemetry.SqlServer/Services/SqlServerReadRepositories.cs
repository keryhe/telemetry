using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data.Read;

namespace Keryhe.Telemetry.SqlServer.Services;

// =============================================================================
// SqlServer read repositories — separate dialect implementations.
// Connection comes from ConnectionStrings:Read. The shared base classes hold the
// dialect-neutral read SQL + shaping; only the alert CRUD/cooldown SQL differs.
// =============================================================================

public class SqlServerTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : TraceReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Read")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}

public class SqlServerMetricReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : MetricReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Read")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}

public class SqlServerLogReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : LogReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Read")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    // SqlServer dialect: LIKE is case-insensitive under the default collation, JSON is read via
    // JSON_VALUE, paging uses OFFSET/FETCH, and LIKE wildcards escape with square brackets.
    protected override string LikeOperator => "LIKE";
    protected override string ResourceServiceNameExpr => "JSON_VALUE(r.attributes_json, '$.\"service.name\"')";
    protected override string PagingClause => "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";
    protected override string EscapeLike(string value)
        => value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
}

public class SqlServerTenantCatalogRepository(IConfiguration configuration)
    : TenantCatalogRepositoryBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Read")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}

public class SqlServerAlertRuleRepository(IConfiguration configuration, ITenantContext tenantContext)
    : AlertRuleRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Read")!;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    // SqlServer stores JSON in nvarchar(max); no cast required.
    protected override string JsonParam(string parameterName) => $"@{parameterName}";

    protected override string ReturningIdentity => "; SELECT CAST(SCOPE_IDENTITY() AS INT)";

    protected override string ClaimFireSql => """
        UPDATE alert_rules
        SET last_fired_at = GETUTCDATE()
        WHERE id = @ruleId
          AND tenant_id = @tenantId
          AND enabled = 1
          AND (last_fired_at IS NULL
               OR last_fired_at < DATEADD(MINUTE, -@cooldownMinutes, GETUTCDATE()))
        """;
}
