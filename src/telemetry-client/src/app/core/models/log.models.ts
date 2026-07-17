export interface LogRecord {
  timeUnixNano: number | null;
  observedTimeUnixNano: number | null;
  severityNumber: number | null;
  severityText: string | null;
  eventName: string | null;
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

// Canonical severity palette (matches the Blazor reference: badges + charts agree).
export const SEVERITY_COLORS: Record<string, string> = {
  Trace: '#9C27B0', Debug: '#2196F3', Info: '#4CAF50',
  Warn: '#FF9800', Error: '#F44336', Fatal: '#B71C1C',
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

const SEVERITY_BG: Record<string, string> = {
  Warn:  'rgba(255, 152, 0,   0.12)',
  Error: 'rgba(244, 67,  54,  0.12)',
  Fatal: 'rgba(183, 28,  28,  0.12)',
};

export function getSeverityBg(num: number | null): string {
  return SEVERITY_BG[getSeverityLabel(num)] ?? '';
}
