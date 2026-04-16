using System.Text.Json;
using Keryhe.Telemetry.Alerting.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Alerting.Evaluators;

public class LogSeveritySpikeEvaluator : IAlertEvaluator
{
    private readonly ILogReadRepository _logs;
    private readonly ILogger<LogSeveritySpikeEvaluator> _logger;

    public AlertRuleType SupportedType => AlertRuleType.LogSeveritySpike;

    public LogSeveritySpikeEvaluator(ILogReadRepository logs, ILogger<LogSeveritySpikeEvaluator> logger)
    {
        _logs = logs;
        _logger = logger;
    }

    public async Task<AlertResult> EvaluateAsync(AlertRule rule, DateTime now, CancellationToken ct = default)
    {
        LogSeveritySpikeCondition condition;
        try
        {
            condition = JsonSerializer.Deserialize<LogSeveritySpikeCondition>(rule.ConditionJson)!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse condition for rule {RuleId}.", rule.Id);
            return AlertResult.NotFiring();
        }

        var windowStart = now.AddMinutes(-condition.WindowMinutes);

        var records = await _logs.GetLogRecordsBySeverityAsync(condition.MinSeverity, windowStart, now, ct);

        if (!string.IsNullOrEmpty(rule.ServiceName))
        {
            records = records.Where(l =>
            {
                var serviceName = l.Resource?.Attributes?.TryGetValue("service.name", out var s) == true
                    ? s?.ToString()
                    : null;
                return serviceName == rule.ServiceName;
            });
        }

        var count = records.Count();

        if (count <= condition.CountThreshold)
            return AlertResult.NotFiring();

        var serviceLabel = rule.ServiceName ?? "all services";
        return AlertResult.Firing(
            $"{count} log records with severity >= {condition.MinSeverity} in last {condition.WindowMinutes} min for {serviceLabel} " +
            $"(threshold: {condition.CountThreshold}).");
    }
}
