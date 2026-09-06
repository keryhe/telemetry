using System.Text.Json;
using Keryhe.Telemetry.Alerting.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Alerting.Evaluators;

public class MetricThresholdEvaluator : IAlertEvaluator
{
    private readonly IMetricReadRepository _metrics;
    private readonly ILogger<MetricThresholdEvaluator> _logger;

    public AlertRuleType SupportedType => AlertRuleType.MetricThreshold;

    public MetricThresholdEvaluator(IMetricReadRepository metrics, ILogger<MetricThresholdEvaluator> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<AlertResult> EvaluateAsync(AlertRule rule, DateTime now, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rule.ServiceName))
        {
            _logger.LogWarning("MetricThreshold rule {RuleId} has no ServiceName — skipping.", rule.Id);
            return AlertResult.NotFiring();
        }

        MetricThresholdCondition condition;
        try
        {
            condition = JsonSerializer.Deserialize<MetricThresholdCondition>(rule.ConditionJson)!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse condition for rule {RuleId}.", rule.Id);
            return AlertResult.NotFiring();
        }

        var latestValues = await _metrics.GetLatestMetricValuesAsync(rule.ServiceName, ct);

        if (!latestValues.TryGetValue(condition.MetricName, out var value))
            return AlertResult.NotFiring();

        var firing = condition.Operator switch
        {
            ">"  => value > condition.Threshold,
            "<"  => value < condition.Threshold,
            ">=" => value >= condition.Threshold,
            "<=" => value <= condition.Threshold,
            _    => false
        };

        if (!firing)
            return AlertResult.NotFiring();

        return AlertResult.Firing(
            $"Metric '{condition.MetricName}' value {value:F2} {condition.Operator} threshold {condition.Threshold:F2} for service '{rule.ServiceName}'.");
    }
}
