using System.Text.Json;
using Keryhe.Telemetry.Api.Alerting.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Api.Alerting.Evaluators;

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

        // Reads the summary tables when the window is unfiltered-by-search/duration (decision 32)
        // instead of a capped GetTracesBy*Async list -- requestCount/errorCount cover every
        // inbound-request trace in the window, not a sample.
        var summary = await _traces.GetTraceSummaryAsync(new TraceSummaryQuery
        {
            Start = windowStart,
            End = now,
            Service = rule.ServiceName,
            BucketCount = 1
        }, ct);

        var total = summary.Summary.Count;
        if (total == 0)
            return AlertResult.NotFiring();

        var errorCount = summary.Summary.ErrorCount;
        var errorRate = (double)errorCount / total * 100.0;

        if (errorRate <= condition.ThresholdPercent)
            return AlertResult.NotFiring();

        var serviceLabel = rule.ServiceName ?? "all services";
        return AlertResult.Firing(
            $"Error rate {errorRate:F1}% exceeded threshold {condition.ThresholdPercent:F1}% " +
            $"({errorCount}/{total} traces with errors in last {condition.WindowMinutes} min for {serviceLabel}).");
    }
}
