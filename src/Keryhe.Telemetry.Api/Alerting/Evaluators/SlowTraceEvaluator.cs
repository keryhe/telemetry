using System.Text.Json;
using Keryhe.Telemetry.Api.Alerting.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Api.Alerting.Evaluators;

public class SlowTraceEvaluator : IAlertEvaluator
{
    private readonly ITraceReadRepository _traces;
    private readonly IRollupReadRepository _rollups;
    private readonly ILogger<SlowTraceEvaluator> _logger;

    public AlertRuleType SupportedType => AlertRuleType.SlowTrace;

    public SlowTraceEvaluator(ITraceReadRepository traces, IRollupReadRepository rollups, ILogger<SlowTraceEvaluator> logger)
    {
        _traces = traces;
        _rollups = rollups;
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

        // Stays on raw spans (plans/summary-rollups.md, decision 11): an exact count of inbound spans at or over
        // the threshold in the rule's window. A doubling duration band straddling the threshold would make a
        // rollup count noticeably wrong near it, and alert windows are minutes long, which was never the
        // timeout problem.
        var windowStart = now.AddMinutes(-condition.WindowMinutes);
        var slow = await _traces.CountSlowInboundSpansAsync(windowStart, now, rule.ServiceName, condition.MinDurationMs, ct);
        if (slow.TimedOut)
        {
            _logger.LogWarning("Slow-request rule {RuleId}: the count timed out; not evaluated this run.", rule.Id);
            return AlertResult.NotFiring();
        }
        if (slow.Count == 0)
            return AlertResult.NotFiring();

        // The message's p99 comes from the request rollup for the same window (approximate, marked with a tilde);
        // omitted when that read times out.
        var p99 = "";
        var read = await _rollups.GetRequestRollupAsync(new RollupQuery
        {
            StartNano = TimeConversion.DateTimeToUnixNano(windowStart),
            EndNano = TimeConversion.DateTimeToUnixNano(now),
            BucketSeconds = Math.Max(1, condition.WindowMinutes) * 60L,
            Service = rule.ServiceName
        }, ct);
        if (!read.TimedOut && read.Rows.Count > 0)
        {
            var bands = new long[DurationBands.Count];
            foreach (var row in read.Rows)
                for (var i = 0; i < bands.Length; i++) bands[i] += row.Bands[i];
            p99 = $" p99 ≈ {DurationBands.Percentile(bands, 0.99, read.Rows.Max(r => r.MaxDurationNanos)) / 1e6:F0}ms.";
        }

        var serviceLabel = rule.ServiceName ?? "all services";
        return AlertResult.Firing(
            $"{slow.Count} slow request(s) over {condition.MinDurationMs}ms in last {condition.WindowMinutes} min for {serviceLabel}.{p99}");
    }
}
