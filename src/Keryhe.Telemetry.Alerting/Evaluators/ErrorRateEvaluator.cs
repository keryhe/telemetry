using System.Text.Json;
using Keryhe.Telemetry.Alerting.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Alerting.Evaluators;

public class ErrorRateEvaluator : IAlertEvaluator
{
    private readonly ITraceReadRepository _traces;
    private readonly ILogger<ErrorRateEvaluator> _logger;

    public AlertRuleType SupportedType => AlertRuleType.ErrorRate;

    public ErrorRateEvaluator(ITraceReadRepository traces, ILogger<ErrorRateEvaluator> logger)
    {
        _traces = traces;
        _logger = logger;
    }

    public async Task<AlertResult> EvaluateAsync(AlertRule rule, DateTime now, CancellationToken ct = default)
    {
        ErrorRateCondition condition;
        try
        {
            condition = JsonSerializer.Deserialize<ErrorRateCondition>(rule.ConditionJson)!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse condition for rule {RuleId}.", rule.Id);
            return AlertResult.NotFiring();
        }

        var windowStart = now.AddMinutes(-condition.WindowMinutes);
        const int limit = 5000;

        List<TraceInfo> traces;
        if (!string.IsNullOrEmpty(rule.ServiceName))
            traces = await _traces.GetTracesByServiceAsync(rule.ServiceName, windowStart, now, limit, ct);
        else
            traces = await _traces.GetTracesByTimeRangeAsync(windowStart, now, limit, ct);

        if (traces.Count == 0)
            return AlertResult.NotFiring();

        var errorCount = traces.Count(t => t.HasErrors);
        var errorRate = (double)errorCount / traces.Count * 100.0;

        if (errorRate <= condition.ThresholdPercent)
            return AlertResult.NotFiring();

        var serviceLabel = rule.ServiceName ?? "all services";
        return AlertResult.Firing(
            $"Error rate {errorRate:F1}% exceeded threshold {condition.ThresholdPercent:F1}% " +
            $"({errorCount}/{traces.Count} traces with errors in last {condition.WindowMinutes} min for {serviceLabel}).");
    }
}
