using System.Text.Json;
using Keryhe.Telemetry.Api.Alerting.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Api.Alerting.Evaluators;

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

        // GetLogSummaryAsync's rollup path can't answer an arbitrary severity cutoff (see
        // LogReadRepositoryBase's own note on why MinSeverity always falls back to raw), but this
        // evaluator's rule condition IS a MinSeverity filter by definition — so this always takes
        // the raw path today. Still a large improvement over the retired
        // GetLogRecordsBySeverityAsync, which loaded every matching row just to count them; this
        // reads a SQL GROUP BY aggregate instead.
        var summary = await _logs.GetLogSummaryAsync(new LogSummaryQuery
        {
            Start = windowStart,
            End = now,
            Service = rule.ServiceName,
            MinSeverity = condition.MinSeverity,
            BucketCount = 1
        }, ct);

        var count = summary.Buckets.Sum(b => b.Trace + b.Debug + b.Info + b.Warn + b.Error + b.Fatal);

        if (count <= condition.CountThreshold)
            return AlertResult.NotFiring();

        var serviceLabel = rule.ServiceName ?? "all services";
        return AlertResult.Firing(
            $"{count} log records with severity >= {condition.MinSeverity} in last {condition.WindowMinutes} min for {serviceLabel} " +
            $"(threshold: {condition.CountThreshold}).");
    }
}
