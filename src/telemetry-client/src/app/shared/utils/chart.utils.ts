export interface TimeBucket {
  timestamp: Date;
  count: number;
  errorCount: number;
}

export function bucketTraces(
  items: { traceStartTime: string; hasErrors: boolean }[],
  start: Date,
  end: Date,
  bucketCount = 24
): TimeBucket[] {
  const buckets: TimeBucket[] = Array.from({ length: bucketCount }, (_, i) => ({
    timestamp: new Date(start.getTime() + (i / bucketCount) * (end.getTime() - start.getTime())),
    count: 0,
    errorCount: 0,
  }));

  const rangeMs = end.getTime() - start.getTime();
  for (const item of items) {
    const t = new Date(item.traceStartTime).getTime();
    const idx = Math.min(bucketCount - 1, Math.floor(((t - start.getTime()) / rangeMs) * bucketCount));
    if (idx >= 0) {
      buckets[idx].count++;
      if (item.hasErrors) buckets[idx].errorCount++;
    }
  }
  return buckets;
}

export interface LogBucket {
  time: Date;
  errors: number;
  warnings: number;
  info: number;
}

export function bucketLogs(
  items: { timeUnixNano: number | null; severityNumber: number | null }[],
  start: Date,
  end: Date,
  bucketCount = 24
): LogBucket[] {
  const buckets: LogBucket[] = Array.from({ length: bucketCount }, (_, i) => ({
    time: new Date(start.getTime() + (i / bucketCount) * (end.getTime() - start.getTime())),
    errors: 0,
    warnings: 0,
    info: 0,
  }));
  const rangeMs = end.getTime() - start.getTime() || 1;
  const startMs = start.getTime();

  for (const item of items) {
    if (!item.timeUnixNano) continue;
    const tMs = item.timeUnixNano / 1_000_000;
    const idx = Math.min(bucketCount - 1, Math.max(0, Math.floor(((tMs - startMs) / rangeMs) * bucketCount)));
    const sev = item.severityNumber ?? 9;
    if (sev >= 17) buckets[idx].errors++;
    else if (sev >= 13) buckets[idx].warnings++;
    else buckets[idx].info++;
  }
  return buckets;
}

function getSeverityGroup(num: number | null): string {
  if (!num) return 'Info';
  if (num <= 4) return 'Trace';
  if (num <= 8) return 'Debug';
  if (num <= 12) return 'Info';
  if (num <= 16) return 'Warn';
  if (num <= 20) return 'Error';
  return 'Fatal';
}

export function formatDuration(ms: number): string {
  if (ms < 1) return `${(ms * 1000).toFixed(0)}µs`;
  if (ms < 1000) return `${ms.toFixed(1)}ms`;
  return `${(ms / 1000).toFixed(2)}s`;
}

export function parseDotnetTimespan(ts: string): number {
  // Handles formats like "00:00:01.234" or "1.00:00:00"
  const parts = ts.split(':');
  if (parts.length === 3) {
    const h = parseFloat(parts[0]);
    const m = parseFloat(parts[1]);
    const s = parseFloat(parts[2]);
    return (h * 3600 + m * 60 + s) * 1000;
  }
  return 0;
}
