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

// Condition shapes stored in conditionJson per rule type
export interface MetricThresholdCondition {
  metricName: string;
  operator: string;
  threshold: number;
}

export interface ThresholdCondition {
  threshold: number;
}

export function parseCondition(rule: AlertRule): MetricThresholdCondition | ThresholdCondition | null {
  try {
    return JSON.parse(rule.conditionJson);
  } catch {
    return null;
  }
}
