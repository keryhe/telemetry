using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

public interface IAlertRuleRepository
{
    Task<List<AlertRule>> GetEnabledRulesAsync(CancellationToken ct = default);
    Task<List<AlertRule>> GetAllRulesAsync(CancellationToken ct = default);
    Task<AlertRule> CreateRuleAsync(AlertRule rule, CancellationToken ct = default);
    Task<AlertRule> UpdateRuleAsync(AlertRule rule, CancellationToken ct = default);
    Task DeleteRuleAsync(int id, CancellationToken ct = default);
    Task<bool> TryClaimFireAsync(int ruleId, int cooldownMinutes, CancellationToken ct = default);
    Task AddAlertEventAsync(AlertEvent alertEvent, CancellationToken ct = default);
    Task<List<AlertEvent>> GetRecentAlertEventsAsync(int limit = 50, CancellationToken ct = default);
}
