using Dapper;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="IAlertRuleRepository"/> (CRUD + cooldown claim +
/// recent events). The dialect-specific pieces — JSON column casts, identity return, and
/// the atomic cooldown UPDATE — are supplied by the provider subclass. This replaces the
/// former EF implementation whose <c>TryClaimFireAsync</c> hardcoded Postgres SQL.
/// </summary>
public abstract class AlertRuleRepositoryBase : DapperReadRepository, IAlertRuleRepository
{
    protected AlertRuleRepositoryBase(ITenantContext tenantContext) : base(tenantContext) { }

    /// <summary>Wraps a parameter for a JSON column (e.g. <c>CAST(@p AS jsonb)</c> for Postgres, <c>@p</c> for SqlServer).</summary>
    protected abstract string JsonParam(string parameterName);

    /// <summary>Clause appended to an INSERT to return the generated identity as the scalar result.</summary>
    protected abstract string ReturningIdentity { get; }

    /// <summary>Atomic cooldown-aware claim UPDATE. Parameters: @ruleId, @tenantId, @cooldownMinutes.</summary>
    protected abstract string ClaimFireSql { get; }

    private const string RuleColumns =
        "id, tenant_id, name, type, service_name, condition_json, webhook_url, cooldown_minutes, enabled, created_at, last_fired_at";

    public async Task<List<long>> GetEnabledTenantIdsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var ids = await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT DISTINCT tenant_id FROM alert_rules WHERE enabled = @enabled",
            new { enabled = true }, cancellationToken: ct));
        return ids.ToList();
    }

    public Task<List<AlertRule>> GetEnabledRulesAsync(CancellationToken ct = default)
        => GetEnabledRulesAsync(TenantId, ct);

    public async Task<List<AlertRule>> GetEnabledRulesAsync(long tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AlertRuleRow>(new CommandDefinition(
            $"SELECT {RuleColumns} FROM alert_rules WHERE enabled = @enabled AND tenant_id = @tenantId",
            new { enabled = true, tenantId }, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<List<AlertRule>> GetAllRulesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AlertRuleRow>(new CommandDefinition(
            $"SELECT {RuleColumns} FROM alert_rules WHERE tenant_id = @tenantId ORDER BY created_at DESC",
            new { tenantId = TenantId }, cancellationToken: ct));
        return rows.Select(MapToDomain).ToList();
    }

    public async Task<AlertRule> CreateRuleAsync(AlertRule rule, CancellationToken ct = default)
    {
        var tenantId = TenantId;
        var createdAt = DateTime.UtcNow;

        var sql = $"""
            INSERT INTO alert_rules (tenant_id, name, type, service_name, condition_json, webhook_url, cooldown_minutes, enabled, created_at)
            VALUES (@tenantId, @name, @type, @serviceName, {JsonParam("conditionJson")}, @webhookUrl, @cooldownMinutes, @enabled, @createdAt)
            {ReturningIdentity}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
        {
            tenantId,
            name = rule.Name,
            type = rule.Type.ToString(),
            serviceName = rule.ServiceName,
            conditionJson = rule.ConditionJson,
            webhookUrl = rule.WebhookUrl,
            cooldownMinutes = rule.CooldownMinutes,
            enabled = rule.Enabled,
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

    public async Task<AlertRule> UpdateRuleAsync(AlertRule rule, CancellationToken ct = default)
    {
        var tenantId = TenantId;
        var sql = $"""
            UPDATE alert_rules
            SET name = @name, type = @type, service_name = @serviceName, condition_json = {JsonParam("conditionJson")},
                webhook_url = @webhookUrl, cooldown_minutes = @cooldownMinutes, enabled = @enabled
            WHERE id = @id AND tenant_id = @tenantId
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            id = rule.Id,
            tenantId,
            name = rule.Name,
            type = rule.Type.ToString(),
            serviceName = rule.ServiceName,
            conditionJson = rule.ConditionJson,
            webhookUrl = rule.WebhookUrl,
            cooldownMinutes = rule.CooldownMinutes,
            enabled = rule.Enabled
        }, cancellationToken: ct));

        if (affected == 0)
            throw new InvalidOperationException($"Alert rule {rule.Id} not found.");

        var row = await conn.QuerySingleAsync<AlertRuleRow>(new CommandDefinition(
            $"SELECT {RuleColumns} FROM alert_rules WHERE id = @id AND tenant_id = @tenantId",
            new { id = rule.Id, tenantId }, cancellationToken: ct));
        return MapToDomain(row);
    }

    public async Task DeleteRuleAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM alert_rules WHERE id = @id AND tenant_id = @tenantId",
            new { id, tenantId = TenantId }, cancellationToken: ct));
    }

    public async Task<bool> TryClaimFireAsync(int ruleId, long tenantId, int cooldownMinutes, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            ClaimFireSql, new { ruleId, tenantId, cooldownMinutes }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task AddAlertEventAsync(AlertEvent alertEvent, CancellationToken ct = default)
    {
        var sql = $"""
            INSERT INTO alert_events (rule_id, fired_at, details_json)
            VALUES (@ruleId, @firedAt, {JsonParam("detailsJson")})
            """;
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            ruleId = alertEvent.RuleId,
            firedAt = alertEvent.FiredAt,
            detailsJson = alertEvent.DetailsJson
        }, cancellationToken: ct));
    }

    public async Task<List<AlertEvent>> GetRecentAlertEventsAsync(int limit = 50, CancellationToken ct = default)
    {
        var sql = """
            SELECT
                e.id AS EvId, e.rule_id AS EvRuleId, e.fired_at AS EvFiredAt, e.details_json AS EvDetailsJson,
                r.id AS RuId, r.tenant_id AS RuTenantId, r.name AS RuName, r.type AS RuType, r.service_name AS RuServiceName,
                r.condition_json AS RuConditionJson, r.webhook_url AS RuWebhookUrl, r.cooldown_minutes AS RuCooldownMinutes,
                r.enabled AS RuEnabled, r.created_at AS RuCreatedAt, r.last_fired_at AS RuLastFiredAt
            FROM alert_events e
            JOIN alert_rules r ON e.rule_id = r.id
            WHERE r.tenant_id = @tenantId
            ORDER BY e.fired_at DESC
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RecentEventRow>(new CommandDefinition(sql, new { tenantId = TenantId }, cancellationToken: ct));

        return rows.Take(limit).Select(e => new AlertEvent
        {
            Id = e.EvId,
            RuleId = e.EvRuleId,
            FiredAt = e.EvFiredAt,
            DetailsJson = e.EvDetailsJson,
            Rule = new AlertRule
            {
                Id = e.RuId,
                TenantId = e.RuTenantId,
                Name = e.RuName,
                Type = Enum.Parse<AlertRuleType>(e.RuType),
                ServiceName = e.RuServiceName,
                ConditionJson = e.RuConditionJson,
                WebhookUrl = e.RuWebhookUrl,
                CooldownMinutes = e.RuCooldownMinutes,
                Enabled = e.RuEnabled,
                CreatedAt = e.RuCreatedAt,
                LastFiredAt = e.RuLastFiredAt
            }
        }).ToList();
    }

    private static AlertRule MapToDomain(AlertRuleRow e) => new()
    {
        Id = e.Id,
        TenantId = e.TenantId,
        Name = e.Name,
        Type = Enum.Parse<AlertRuleType>(e.Type),
        ServiceName = e.ServiceName,
        ConditionJson = e.ConditionJson,
        WebhookUrl = e.WebhookUrl,
        CooldownMinutes = e.CooldownMinutes,
        Enabled = e.Enabled,
        CreatedAt = e.CreatedAt,
        LastFiredAt = e.LastFiredAt
    };

    private sealed class AlertRuleRow
    {
        public int Id { get; set; }
        public long TenantId { get; set; }
        public string Name { get; set; } = null!;
        public string Type { get; set; } = null!;
        public string? ServiceName { get; set; }
        public string ConditionJson { get; set; } = null!;
        public string WebhookUrl { get; set; } = null!;
        public int CooldownMinutes { get; set; }
        public bool Enabled { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastFiredAt { get; set; }
    }

    private sealed class RecentEventRow
    {
        public long EvId { get; set; }
        public int EvRuleId { get; set; }
        public DateTime EvFiredAt { get; set; }
        public string EvDetailsJson { get; set; } = null!;
        public int RuId { get; set; }
        public long RuTenantId { get; set; }
        public string RuName { get; set; } = null!;
        public string RuType { get; set; } = null!;
        public string? RuServiceName { get; set; }
        public string RuConditionJson { get; set; } = null!;
        public string RuWebhookUrl { get; set; } = null!;
        public int RuCooldownMinutes { get; set; }
        public bool RuEnabled { get; set; }
        public DateTime RuCreatedAt { get; set; }
        public DateTime? RuLastFiredAt { get; set; }
    }
}
