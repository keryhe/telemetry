using System.Text.Json;
using Keryhe.Telemetry.Api.Alerting.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Api.Alerting.Evaluators;

public class ErrorRateEvaluator : IAlertEvaluator
{
    private readonly IRollupReadRepository _rollups;
    private readonly RollupOptions _rollupOptions;
    private readonly ILogger<ErrorRateEvaluator> _logger;

    public AlertRuleType SupportedType => AlertRuleType.ErrorRate;

    public ErrorRateEvaluator(IRollupReadRepository rollups, IOptions<RollupOptions> rollupOptions, ILogger<ErrorRateEvaluator> logger)
    {
        _rollups = rollups;
        _rollupOptions = rollupOptions.Value;
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

        // Request rollup over whole minutes ending at writtenThrough (plans/summary-rollups.md): the window
        // trails the clock by the rollup's write margin, so a 1-minute rule always reads one full minute.
        var writtenThrough = TimeConversion.DateTimeToUnixNano(RollupSummaryBuilder.WrittenThrough(now, _rollupOptions));
        var read = await _rollups.GetRequestRollupAsync(new RollupQuery
        {
            StartNano = writtenThrough - condition.WindowMinutes * 60_000_000_000L,
            EndNano = writtenThrough,
            BucketSeconds = Math.Max(1, condition.WindowMinutes) * 60L,
            Service = rule.ServiceName
        }, ct);
        if (read.TimedOut)
        {
            _logger.LogWarning("Error-rate rule {RuleId}: the rollup read timed out; not evaluated this run.", rule.Id);
            return AlertResult.NotFiring();
        }

        var total = read.Rows.Sum(r => r.RequestCount);
        if (total == 0)
            return AlertResult.NotFiring();

        var errorCount = read.Rows.Sum(r => r.ErrorCount);
        var errorRate = (double)errorCount / total * 100.0;

        if (errorRate <= condition.ThresholdPercent)
            return AlertResult.NotFiring();

        var serviceLabel = rule.ServiceName ?? "all services";
        return AlertResult.Firing(
            $"Error rate {errorRate:F1}% exceeded threshold {condition.ThresholdPercent:F1}% " +
            $"({errorCount}/{total} requests with errors in last {condition.WindowMinutes} min for {serviceLabel}).");
    }
}
