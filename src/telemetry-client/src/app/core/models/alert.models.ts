export interface Tenant {
  id: number;
  name: string;
}

export enum AlertRuleType {
  MetricThreshold = 0,
  ErrorRate = 1,
  SlowTrace = 2,
  LogSeveritySpike = 3,
}

export const ALERT_RULE_TYPE_LABELS: { [key: number]: string } = {
  [AlertRuleType.MetricThreshold]: 'Metric Threshold',
  [AlertRuleType.ErrorRate]: 'Error Rate',
  [AlertRuleType.SlowTrace]: 'Slow Trace',
  [AlertRuleType.LogSeveritySpike]: 'Log Severity Spike',
};

export interface AlertRule {
  id: number;
  tenantId: number;
  name: string;
  type: AlertRuleType;
  serviceName: string | null;
  conditionJson: string;
  webhookUrl: string;
  cooldownMinutes: number;
  enabled: boolean;
  createdAt: string;
  lastFiredAt: string | null;
}

export interface AlertEvent {
  id: number;
  ruleId: number;
  firedAt: string;
  detailsJson: string;
  rule: AlertRule | null;
}

// Condition shapes stored in conditionJson per rule type.
// NOTE: keys are PascalCase to match the server's System.Text.Json (case-sensitive)
// deserialization into Keryhe.Telemetry.Alerting.Models.*Condition.
export interface MetricThresholdCondition {
  MetricName: string;
  Operator: string; // ">", "<", ">=", "<="
  Threshold: number;
}

export interface ErrorRateCondition {
  ThresholdPercent: number;
  WindowMinutes: number;
}

export interface SlowTraceCondition {
  MinDurationMs: number;
  WindowMinutes: number;
}

export interface LogSeveritySpikeCondition {
  MinSeverity: number;
  CountThreshold: number;
  WindowMinutes: number;
}

export type AlertCondition =
  | MetricThresholdCondition
  | ErrorRateCondition
  | SlowTraceCondition
  | LogSeveritySpikeCondition;

export function parseCondition(rule: AlertRule): Record<string, unknown> | null {
  try {
    return JSON.parse(rule.conditionJson);
  } catch {
    return null;
  }
}

function severityLabel(minSeverity: number): string {
  if (minSeverity <= 5) return 'DEBUG';
  if (minSeverity <= 9) return 'INFO';
  if (minSeverity <= 13) return 'WARN';
  if (minSeverity <= 17) return 'ERROR';
  return 'FATAL';
}

/** Human-readable condition summary per rule type. Mirrors Blazor's FormatCondition. */
export function formatCondition(rule: AlertRule): string {
  try {
    const c = JSON.parse(rule.conditionJson) as Record<string, unknown>;
    switch (rule.type) {
      case AlertRuleType.MetricThreshold:
        return `${c['MetricName']} ${c['Operator']} ${c['Threshold']}`;
      case AlertRuleType.ErrorRate:
        return `Error rate > ${c['ThresholdPercent']}% in ${c['WindowMinutes']} min`;
      case AlertRuleType.SlowTrace:
        return `Duration > ${c['MinDurationMs']}ms in ${c['WindowMinutes']} min`;
      case AlertRuleType.LogSeveritySpike:
        return `> ${c['CountThreshold']} ${severityLabel(Number(c['MinSeverity']))}+ logs in ${c['WindowMinutes']} min`;
    }
  } catch { /* fall through */ }
  return rule.conditionJson || '—';
}
