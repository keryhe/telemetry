using System.Text.Json;
using Keryhe.Telemetry.Api.Alerting.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Api.Alerting.Evaluators;

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

        // Counts anchors (roots plus orphan_roots) whose OWN duration is >= threshold (decision 4:
        // mode=slow filters on the anchor span's duration, not whole-trace) -- always the raw path,
        // through the covering idx_spans_root_time, instead of reading a capped 100-row list
        // (decision 32).
        var summary = await _traces.GetTraceSummaryAsync(new TraceSummaryQuery
        {
            Start = windowStart,
            End = now,
            Mode = "slow",
            MinDurationMs = condition.MinDurationMs,
            Service = rule.ServiceName,
            BucketCount = 1
        }, ct);

        var count = summary.Summary.Count;
        if (count == 0)
            return AlertResult.NotFiring();

        var serviceLabel = rule.ServiceName ?? "all services";
        return AlertResult.Firing(
            $"{count} trace(s) exceeded {condition.MinDurationMs}ms in last {condition.WindowMinutes} min for {serviceLabel}. " +
            $"p99: {summary.Summary.P99Ms:F0}ms.");
    }
}
