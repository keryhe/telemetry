using System.Data.Common;
using ClickHouse.Client.ADO;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data.Read;

namespace Keryhe.Telemetry.ClickHouse.Services;

// =============================================================================
// ClickHouse read repositories. The shared Dapper bases hold the dialect-neutral read SQL
// and shape rows in C# (attributes are deserialized from JSON text, so no Postgres `->>` on
// the hot path). ClickHouse overrides only where its dialect differs: JSON extraction and the
// alert-rule CRUD (no identity columns / transactional UPDATE — see below). Connections come
// from ConnectionStrings:Read.
// =============================================================================

internal static class ClickHouseConnectionFactory
{
    public static async Task<DbConnection> OpenReadAsync(string connectionString, CancellationToken ct)
    {
        var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}

public class ClickHouseTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : TraceReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Read")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);
}

public class ClickHouseMetricReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : MetricReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Read")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);
}

public class ClickHouseLogReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : LogReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Read")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    // attributes_json is a Nullable(String) holding JSON text; extract service.name with
    // JSONExtractString (coalesce guards NULL rows). ILIKE, LIMIT/OFFSET paging, and backslash
    // LIKE-escaping all match the Postgres defaults, so those hooks are inherited unchanged.
    protected override string ResourceServiceNameExpr => "JSONExtractString(coalesce(r.attributes_json, ''), 'service.name')";
}

public class ClickHouseTenantCatalogRepository(IConfiguration configuration)
    : TenantCatalogRepositoryBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Read")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);
}

/// <summary>
/// ClickHouse alert-rule repository. The inherited SELECT-based reads work unchanged, but the
/// write path is overridden because ClickHouse has no identity columns, no <c>RETURNING</c>, and
/// no transactional <c>UPDATE</c>: ids are generated in-process, edits use <c>ALTER TABLE ...
/// UPDATE</c> mutations, and <c>TryClaimFireAsync</c> is best-effort (read-check-then-update is
/// not atomic, so under concurrent evaluators a rule could double-fire). Acceptable for the
/// control plane — no host currently drives alert evaluation on a schedule.
/// </summary>
public class ClickHouseAlertRuleRepository(IConfiguration configuration, ITenantContext tenantContext)
    : AlertRuleRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Read")!;

    // In-process id generators (ClickHouse has no auto-increment). Seeded from the clock so ids
    // stay unique-enough across restarts for these low-volume control-plane tables.
    private static int _ruleSeq = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0x3FFFFFFF);
    private static int NextRuleId() => Interlocked.Increment(ref _ruleSeq);

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    // JSON columns are plain String in ClickHouse — no cast. ReturningIdentity/ClaimFireSql are
    // abstract in the base but unused here (Create/TryClaim are fully overridden below).
    protected override string JsonParam(string parameterName) => $"@{parameterName}";
    protected override string ReturningIdentity => string.Empty;
    protected override string ClaimFireSql => string.Empty;

    public override async Task<AlertRule> CreateRuleAsync(AlertRule rule, CancellationToken ct = default)
    {
        var tenantId = TenantId;
        var createdAt = DateTime.UtcNow;
        var id = NextRuleId();

        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO alert_rules
                (id, tenant_id, name, type, service_name, condition_json, webhook_url, cooldown_minutes, enabled, created_at)
            VALUES
                (@id, @tenantId, @name, @type, @serviceName, @conditionJson, @webhookUrl, @cooldownMinutes, @enabled, @createdAt)
            """, new
        {
            id,
            tenantId,
            name = rule.Name,
            type = rule.Type.ToString(),
            serviceName = rule.ServiceName,
            conditionJson = rule.ConditionJson,
            webhookUrl = rule.WebhookUrl,
            cooldownMinutes = rule.CooldownMinutes,
            enabled = (byte)(rule.Enabled ? 1 : 0),
            createdAt
        }, cancellationToken: ct));

        return new AlertRule
        {
            Id = id,
            TenantId = tenantId,
            Name = rule.Name,
            Type = rule.Type,
            ServiceName = rule.ServiceName,
            ConditionJson = rule.ConditionJson,
            WebhookUrl = rule.WebhookUrl,
            CooldownMinutes = rule.CooldownMinutes,
            Enabled = rule.Enabled,
            CreatedAt = createdAt,
            LastFiredAt = null
        };
    }

    public override async Task<AlertRule> UpdateRuleAsync(AlertRule rule, CancellationToken ct = default)
    {
        var tenantId = TenantId;

        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            ALTER TABLE alert_rules UPDATE
                name = @name, type = @type, service_name = @serviceName, condition_json = @conditionJson,
                webhook_url = @webhookUrl, cooldown_minutes = @cooldownMinutes, enabled = @enabled
            WHERE id = @id AND tenant_id = @tenantId
            """, new
        {
            id = rule.Id,
            tenantId,
            name = rule.Name,
            type = rule.Type.ToString(),
            serviceName = rule.ServiceName,
            conditionJson = rule.ConditionJson,
            webhookUrl = rule.WebhookUrl,
            cooldownMinutes = rule.CooldownMinutes,
            enabled = (byte)(rule.Enabled ? 1 : 0)
        }, cancellationToken: ct));

        // The mutation is applied asynchronously; return the caller's intended state rather than
        // re-reading (which could still observe the pre-mutation row).
        return new AlertRule
        {
            Id = rule.Id,
            TenantId = tenantId,
            Name = rule.Name,
            Type = rule.Type,
            ServiceName = rule.ServiceName,
            ConditionJson = rule.ConditionJson,
            WebhookUrl = rule.WebhookUrl,
            CooldownMinutes = rule.CooldownMinutes,
            Enabled = rule.Enabled,
            CreatedAt = rule.CreatedAt,
            LastFiredAt = rule.LastFiredAt
        };
    }

    public override async Task DeleteRuleAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM alert_rules WHERE id = @id AND tenant_id = @tenantId",
            new { id, tenantId = TenantId }, cancellationToken: ct));
    }

    public override async Task<bool> TryClaimFireAsync(int ruleId, long tenantId, int cooldownMinutes, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // Best-effort, NON-ATOMIC cooldown claim: read the last fire time, decide in C#, then
        // mutate. ClickHouse offers no atomic conditional UPDATE, so a concurrent evaluator could
        // also pass this check and double-fire. Acceptable given no scheduled evaluation runs.
        var lastFired = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT last_fired_at FROM alert_rules WHERE id = @ruleId AND tenant_id = @tenantId AND enabled = 1 LIMIT 1",
            new { ruleId, tenantId }, cancellationToken: ct));

        if (lastFired is not null && lastFired.Value > DateTime.UtcNow.AddMinutes(-cooldownMinutes))
            return false;

        await conn.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE alert_rules UPDATE last_fired_at = now64(9) WHERE id = @ruleId AND tenant_id = @tenantId",
            new { ruleId, tenantId }, cancellationToken: ct));
        return true;
    }

    public override async Task AddAlertEventAsync(AlertEvent alertEvent, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO alert_events (id, rule_id, fired_at, details_json)
            VALUES (@id, @ruleId, @firedAt, @detailsJson)
            """, new
        {
            id = RowId.Next(),
            ruleId = alertEvent.RuleId,
            firedAt = alertEvent.FiredAt,
            detailsJson = alertEvent.DetailsJson
        }, cancellationToken: ct));
    }
}
