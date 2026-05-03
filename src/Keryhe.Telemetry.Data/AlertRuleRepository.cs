using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data.Access;
using Keryhe.Telemetry.Data.Access.Models;
using Microsoft.EntityFrameworkCore;

namespace Keryhe.Telemetry.Data;

public class AlertRuleRepository : IAlertRuleRepository
{
    private readonly AlertDbContext _db;
    private readonly ITenantContext _tenantContext;

    public AlertRuleRepository(AlertDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
    }

    public async Task<List<long>> GetEnabledTenantIdsAsync(CancellationToken ct = default)
    {
        return await _db.AlertRules.AsNoTracking()
            .Where(r => r.Enabled)
            .Select(r => r.TenantId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<List<AlertRule>> GetEnabledRulesAsync(CancellationToken ct = default)
    {
        var tenantId = _tenantContext.GetRequiredTenantId();
        return await GetEnabledRulesAsync(tenantId, ct);
    }

    public async Task<List<AlertRule>> GetEnabledRulesAsync(long tenantId, CancellationToken ct = default)
    {
        var entities = await _db.AlertRules.AsNoTracking()
            .Where(r => r.Enabled && r.TenantId == tenantId)
            .ToListAsync(ct);
        return entities.Select(MapToDomain).ToList();
    }

    public async Task<List<AlertRule>> GetAllRulesAsync(CancellationToken ct = default)
    {
        var tenantId = _tenantContext.GetRequiredTenantId();
        var entities = await _db.AlertRules.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(MapToDomain).ToList();
    }

    public async Task<AlertRule> CreateRuleAsync(AlertRule rule, CancellationToken ct = default)
    {
        var tenantId = _tenantContext.GetRequiredTenantId();
        var entity = new AlertRuleEntity
        {
            TenantId = tenantId,
            Name = rule.Name,
            Type = rule.Type.ToString(),
            ServiceName = rule.ServiceName,
            ConditionJson = rule.ConditionJson,
            WebhookUrl = rule.WebhookUrl,
            CooldownMinutes = rule.CooldownMinutes,
            Enabled = rule.Enabled,
            CreatedAt = DateTime.UtcNow
        };
        _db.AlertRules.Add(entity);
        await _db.SaveChangesAsync(ct);
        return MapToDomain(entity);
    }

    public async Task<AlertRule> UpdateRuleAsync(AlertRule rule, CancellationToken ct = default)
    {
        var tenantId = _tenantContext.GetRequiredTenantId();
        var entity = await _db.AlertRules
            .Where(r => r.Id == rule.Id && r.TenantId == tenantId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Alert rule {rule.Id} not found.");

        entity.Name = rule.Name;
        entity.Type = rule.Type.ToString();
        entity.ServiceName = rule.ServiceName;
        entity.ConditionJson = rule.ConditionJson;
        entity.WebhookUrl = rule.WebhookUrl;
        entity.CooldownMinutes = rule.CooldownMinutes;
        entity.Enabled = rule.Enabled;

        await _db.SaveChangesAsync(ct);
        return MapToDomain(entity);
    }

    public async Task DeleteRuleAsync(int id, CancellationToken ct = default)
    {
        var tenantId = _tenantContext.GetRequiredTenantId();
        await _db.AlertRules.Where(r => r.Id == id && r.TenantId == tenantId).ExecuteDeleteAsync(ct);
    }

    public async Task<bool> TryClaimFireAsync(int ruleId, long tenantId, int cooldownMinutes, CancellationToken ct = default)
    {
        var rows = await _db.Database.ExecuteSqlAsync(
            $"""
            UPDATE alert_rules
            SET "last_fired_at" = NOW()
            WHERE "id" = {ruleId}
              AND "tenant_id" = {tenantId}
              AND "enabled" = TRUE
              AND ("last_fired_at" IS NULL
                   OR "last_fired_at" < NOW() - ({cooldownMinutes} * INTERVAL '1 minute'))
            """, ct);
        return rows > 0;
    }

    public async Task AddAlertEventAsync(AlertEvent alertEvent, CancellationToken ct = default)
    {
        var entity = new AlertEventEntity
        {
            RuleId = alertEvent.RuleId,
            FiredAt = alertEvent.FiredAt,
            DetailsJson = alertEvent.DetailsJson
        };
        _db.AlertEvents.Add(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<AlertEvent>> GetRecentAlertEventsAsync(int limit = 50, CancellationToken ct = default)
    {
        var tenantId = _tenantContext.GetRequiredTenantId();
        var entities = await _db.AlertEvents.AsNoTracking()
            .Include(e => e.Rule)
            .Where(e => e.Rule.TenantId == tenantId)
            .OrderByDescending(e => e.FiredAt)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(e => new AlertEvent
        {
            Id = e.Id,
            RuleId = e.RuleId,
            FiredAt = e.FiredAt,
            DetailsJson = e.DetailsJson,
            Rule = e.Rule != null ? MapToDomain(e.Rule) : null
        }).ToList();
    }

    private static AlertRule MapToDomain(AlertRuleEntity e) => new AlertRule
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
}
