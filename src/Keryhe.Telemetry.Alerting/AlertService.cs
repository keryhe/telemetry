using System.Text.Json;
using Keryhe.Telemetry.Alerting.Evaluators;
using Keryhe.Telemetry.Alerting.Notifications;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Alerting;

public class AlertService : IAlertService
{
    private readonly IAlertRuleRepository _ruleRepo;
    private readonly Dictionary<AlertRuleType, IAlertEvaluator> _evaluators;
    private readonly INotificationChannel _notification;
    private readonly ILogger<AlertService> _logger;

    public AlertService(
        IAlertRuleRepository ruleRepo,
        IEnumerable<IAlertEvaluator> evaluators,
        INotificationChannel notification,
        ILogger<AlertService> logger)
    {
        _ruleRepo = ruleRepo;
        _evaluators = evaluators.ToDictionary(e => e.SupportedType);
        _notification = notification;
        _logger = logger;
    }

    public async Task EvaluateAllAsync(CancellationToken ct = default)
    {
        var rules = await _ruleRepo.GetEnabledRulesAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var rule in rules)
        {
            if (ct.IsCancellationRequested)
                break;

            if (rule.LastFiredAt.HasValue &&
                now - rule.LastFiredAt.Value < TimeSpan.FromMinutes(rule.CooldownMinutes))
                continue;

            if (!_evaluators.TryGetValue(rule.Type, out var evaluator))
            {
                _logger.LogWarning("No evaluator registered for rule type {Type}.", rule.Type);
                continue;
            }

            try
            {
                var result = await evaluator.EvaluateAsync(rule, now, ct);
                if (!result.IsFiring)
                    continue;

                // Atomically claim the fire slot — only one instance wins under a load balancer.
                // The UPDATE checks the cooldown and sets last_fired_at in a single statement.
                var claimed = await _ruleRepo.TryClaimFireAsync(rule.Id, rule.CooldownMinutes, ct);
                if (!claimed)
                    continue;

                _logger.LogInformation("Alert rule '{Name}' (Id={Id}) is firing: {Details}", rule.Name, rule.Id, result.Details);

                await _notification.SendAsync(rule, result.Details, now, ct);

                await _ruleRepo.AddAlertEventAsync(new AlertEvent
                {
                    RuleId = rule.Id,
                    FiredAt = now,
                    DetailsJson = JsonSerializer.Serialize(new { details = result.Details })
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error evaluating alert rule {RuleId}.", rule.Id);
            }
        }
    }
}
