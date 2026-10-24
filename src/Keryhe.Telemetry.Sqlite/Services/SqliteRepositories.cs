using System.Data.Common;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Read;

namespace Keryhe.Telemetry.Sqlite.Services;

/// <summary>SQLite tenant catalog: the shared SQL, against the control-plane file.</summary>
public sealed class SqliteTenantCatalogRepository(SqliteControlPlaneConnection controlPlane) : TenantCatalogRepositoryBase
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        await controlPlane.OpenAsync(cancellationToken);
}

/// <summary>
/// SQLite alert rules: the shared CRUD from <see cref="AlertRuleRepositoryBase"/>. JSON columns take the string as is,
/// <c>RETURNING id</c> gives the new identity, timestamps are bound as the fixed-layout text (<see cref="SqliteTime"/>), and the
/// cooldown claim is one <c>UPDATE</c>, atomic under SQLite's single writer.
/// </summary>
public sealed class SqliteAlertRuleRepository(SqliteControlPlaneConnection controlPlane, ITenantContext tenantContext)
    : AlertRuleRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        await controlPlane.OpenAsync(cancellationToken);

    protected override string JsonParam(string parameterName) => $"@{parameterName}";

    protected override string ReturningIdentity => "RETURNING id";

    protected override object TimestampParam(DateTime utc) => SqliteTime.ToColumn(utc);

    protected override string ClaimFireSql => $"""
        UPDATE alert_rules
        SET last_fired_at = {SqliteTime.NowSql}
        WHERE id = @ruleId
          AND tenant_id = @tenantId
          AND enabled = 1
          AND (last_fired_at IS NULL
               OR last_fired_at < {SqliteTime.NowMinusMinutesSql("@cooldownMinutes")})
        """;
}

/// <summary>SQLite retention settings: the shared reads and writes, with the update's timestamp bound as <see cref="SqliteTime"/> text.</summary>
public sealed class SqliteRetentionSettingsRepository(SqliteControlPlaneConnection controlPlane) : RetentionSettingsRepositoryBase
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        await controlPlane.OpenAsync(cancellationToken);

    protected override object TimestampParam(DateTime utc) => SqliteTime.ToColumn(utc);
}
