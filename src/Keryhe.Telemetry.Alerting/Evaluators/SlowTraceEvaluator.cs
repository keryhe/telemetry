using System.Text.Json;
using Keryhe.Telemetry.Alerting.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Alerting.Evaluators;

public class SlowTraceEvaluator : IAlertEvaluator
{
    private readonly ITraceReadRepository _traces;
    private readonly ILogger<SlowTraceEvaluator> _logger;

    public AlertRuleType SupportedType => AlertRuleType.SlowTrace;

    public SlowTraceEvaluator(ITraceReadRepository traces, ILogger<SlowTraceEvaluator> logger)
    {
        _traces = traces;
        _logger = logger;
    }

    public async Task<AlertResult> EvaluateAsync(AlertRule rule, DateTime now, CancellationToken ct = default)
    {
        SlowTraceCondition condition;
        try
        {
            condition = JsonSerializer.Deserialize<SlowTraceCondition>(rule.ConditionJson)!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse condition for rule {RuleId}.", rule.Id);
            return AlertResult.NotFiring();
        }

        var windowStart = now.AddMinutes(-condition.WindowMinutes);
        var minDuration = TimeSpan.FromMilliseconds(condition.MinDurationMs);

        var slowTraces = await _traces.GetSlowTracesAsync(minDuration, windowStart, now, limit: 100, ct);

        if (!string.IsNullOrEmpty(rule.ServiceName))
            slowTraces = slowTraces.Where(t => t.ServiceName == rule.ServiceName).ToList();

        if (slowTraces.Count == 0)
            return AlertResult.NotFiring();

        var maxDurationMs = slowTraces.Max(t => t.TraceDuration.TotalMilliseconds);
        var serviceLabel = rule.ServiceName ?? "all services";
        return AlertResult.Firing(
            $"{slowTraces.Count} trace(s) exceeded {condition.MinDurationMs}ms in last {condition.WindowMinutes} min for {serviceLabel}. " +
            $"Slowest: {maxDurationMs:F0}ms.");
    }
}
