using System.Text.Json;
using Keryhe.Telemetry.Api.Alerting.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Api.Alerting.Evaluators;

public class LogSeveritySpikeEvaluator : IAlertEvaluator
{
    private readonly IRollupReadRepository _rollups;
    private readonly RollupOptions _rollupOptions;
    private readonly ILogger<LogSeveritySpikeEvaluator> _logger;

    public AlertRuleType SupportedType => AlertRuleType.LogSeveritySpike;

    public LogSeveritySpikeEvaluator(IRollupReadRepository rollups, IOptions<RollupOptions> rollupOptions, ILogger<LogSeveritySpikeEvaluator> logger)
    {
        _rollups = rollups;
        _rollupOptions = rollupOptions.Value;
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

        // Log rollup over whole minutes ending at writtenThrough (plans/summary-rollups.md); the severity
        // filter is part of the rollup key, so it is exact.
        var writtenThrough = TimeConversion.DateTimeToUnixNano(RollupSummaryBuilder.WrittenThrough(now, _rollupOptions));
        var read = await _rollups.GetLogRollupAsync(new RollupQuery
        {
            StartNano = writtenThrough - condition.WindowMinutes * 60_000_000_000L,
            EndNano = writtenThrough,
            BucketSeconds = Math.Max(1, condition.WindowMinutes) * 60L,
            Service = rule.ServiceName,
            MinSeverity = condition.MinSeverity
        }, ct);
        if (read.TimedOut)
        {
            _logger.LogWarning("Log-severity rule {RuleId}: the rollup read timed out; not evaluated this run.", rule.Id);
            return AlertResult.NotFiring();
        }

        var count = read.Rows.Sum(r => r.RecordCount);

        if (count <= condition.CountThreshold)
            return AlertResult.NotFiring();

        var serviceLabel = rule.ServiceName ?? "all services";
        return AlertResult.Firing(
            $"{count} log records with severity >= {condition.MinSeverity} in last {condition.WindowMinutes} min for {serviceLabel} " +
            $"(threshold: {condition.CountThreshold}).");
    }
}
