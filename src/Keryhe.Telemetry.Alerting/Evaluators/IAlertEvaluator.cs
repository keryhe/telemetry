using Keryhe.Telemetry.Alerting.Models;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Alerting.Evaluators;

public interface IAlertEvaluator
{
    AlertRuleType SupportedType { get; }
    Task<AlertResult> EvaluateAsync(AlertRule rule, DateTime now, CancellationToken ct = default);
}
