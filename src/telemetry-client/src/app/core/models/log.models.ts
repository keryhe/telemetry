export interface LogRecord {
  timeUnixNano: number | null;
  observedTimeUnixNano: number | null;
  severityNumber: number | null;
  severityText: string | null;
  bodyValue: string | null;
  traceIdHex: string | null;
  spanIdHex: string | null;
  attributes: Record<string, unknown>;
  resource: { attributes: Record<string, unknown> } | null;
  instrumentationScope: { name: string; version: string | null } | null;
}

export function getServiceName(log: LogRecord): string {
  return (log.resource?.attributes?.['service.name'] as string) ?? 'unknown';
}

export function getTimestamp(log: LogRecord): Date {
  return log.timeUnixNano ? new Date(log.timeUnixNano / 1_000_000) : new Date(0);
}

export const SEVERITY_LABELS: Record<number, string> = {
  1: 'Trace', 2: 'Trace', 3: 'Trace', 4: 'Trace',
  5: 'Debug', 6: 'Debug', 7: 'Debug', 8: 'Debug',
  9: 'Info', 10: 'Info', 11: 'Info', 12: 'Info',
  13: 'Warn', 14: 'Warn', 15: 'Warn', 16: 'Warn',
  17: 'Error', 18: 'Error', 19: 'Error', 20: 'Error',
  21: 'Fatal', 22: 'Fatal', 23: 'Fatal', 24: 'Fatal',
};

export const SEVERITY_COLORS: Record<string, string> = {
  Trace: '#9e9e9e', Debug: '#2196f3', Info: '#4caf50',
  Warn: '#ff9800', Error: '#f44336', Fatal: '#9c27b0',
};

export function getSeverityLabel(num: number | null): string {
  if (num === null) return 'Unknown';
  if (num <= 4) return 'Trace';
  if (num <= 8) return 'Debug';
  if (num <= 12) return 'Info';
  if (num <= 16) return 'Warn';
  if (num <= 20) return 'Error';
  return 'Fatal';
}

export function getSeverityColor(num: number | null): string {
  return SEVERITY_COLORS[getSeverityLabel(num)] ?? '#9e9e9e';
}
