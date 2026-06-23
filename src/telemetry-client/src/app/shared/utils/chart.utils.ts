import type { ApexOptions } from 'ng-apexcharts';
import { getSeverityLabel, SEVERITY_COLORS } from '../../core/models/log.models';
import { AggregationTemporality, MetricDataPoint, MetricType } from '../../core/models/metric.models';

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
  trace: number;
  debug: number;
  info: number;
  warn: number;
  error: number;
  fatal: number;
}

export function bucketLogs(
  items: { timeUnixNano: number | null; severityNumber: number | null }[],
  start: Date,
  end: Date,
  bucketCount = 24
): LogBucket[] {
  const buckets: LogBucket[] = Array.from({ length: bucketCount }, (_, i) => ({
    time: new Date(start.getTime() + (i / bucketCount) * (end.getTime() - start.getTime())),
    trace: 0,
    debug: 0,
    info: 0,
    warn: 0,
    error: 0,
    fatal: 0,
  }));
  const rangeMs = end.getTime() - start.getTime() || 1;
  const startMs = start.getTime();

  for (const item of items) {
    if (!item.timeUnixNano) continue;
    const tMs = item.timeUnixNano / 1_000_000;
    const idx = Math.min(bucketCount - 1, Math.max(0, Math.floor(((tMs - startMs) / rangeMs) * bucketCount)));
    // Default null severity to Info (9), matching prior behavior.
    const label = getSeverityLabel(item.severityNumber ?? 9);
    switch (label) {
      case 'Trace': buckets[idx].trace++; break;
      case 'Debug': buckets[idx].debug++; break;
      case 'Warn':  buckets[idx].warn++; break;
      case 'Error': buckets[idx].error++; break;
      case 'Fatal': buckets[idx].fatal++; break;
      default:      buckets[idx].info++; break;
    }
  }
  return buckets;
}

/** Canonical severity palette (Trace → Fatal), derived from the single SEVERITY_COLORS source. */
const LOG_SERIES_COLORS = [
  SEVERITY_COLORS['Trace'], SEVERITY_COLORS['Debug'], SEVERITY_COLORS['Info'],
  SEVERITY_COLORS['Warn'], SEVERITY_COLORS['Error'], SEVERITY_COLORS['Fatal'],
];

/**
 * Shared base config for the stacked log-severity bar chart used by both the
 * Logs page and the Dashboard. Returns 6 series (Trace→Fatal) with numeric
 * epoch categories on a datetime axis. Callers override legend/grid/height.
 */
export function buildLogSeriesOptions(buckets: LogBucket[], isDark: boolean, height: number): ApexOptions {
  return {
    chart: { type: 'bar', height, toolbar: { show: false }, stacked: true, background: 'transparent' },
    theme: { mode: isDark ? 'dark' : 'light' },
    series: [
      { name: 'Trace', data: buckets.map((b) => b.trace) },
      { name: 'Debug', data: buckets.map((b) => b.debug) },
      { name: 'Info',  data: buckets.map((b) => b.info) },
      { name: 'Warn',  data: buckets.map((b) => b.warn) },
      { name: 'Error', data: buckets.map((b) => b.error) },
      { name: 'Fatal', data: buckets.map((b) => b.fatal) },
    ],
    colors: LOG_SERIES_COLORS,
    xaxis: { categories: buckets.map((b) => b.time.getTime()), type: 'datetime' },
    dataLabels: { enabled: false },
    legend: { position: 'top' },
    plotOptions: { bar: { columnWidth: '80%' } },
  };
}

/**
 * A cumulative counter is a monotonic Sum with cumulative temporality. Delta or
 * non-monotonic sums are NOT counters and should render as raw bars.
 */
export function isCounterMetric(type: MetricType, points: MetricDataPoint[]): boolean {
  const first = points[0];
  return type === MetricType.Sum
    && first?.isMonotonic === true
    && first?.aggregationTemporality === AggregationTemporality.Cumulative;
}

/** True for delta-temporality sums (rendered as bars rather than rate). */
export function isDeltaSum(type: MetricType, points: MetricDataPoint[]): boolean {
  return type === MetricType.Sum
    && points[0]?.aggregationTemporality === AggregationTemporality.Delta;
}

/**
 * Per-interval rate of a cumulative counter: Δvalue / Δseconds. Skips counter
 * resets (curr < prev) and non-positive time deltas to avoid negative spikes.
 * Mirrors Blazor's ComputeRateSeries.
 */
export function computeRateSeries(points: MetricDataPoint[]): [number, number][] {
  const result: [number, number][] = [];
  for (let i = 1; i < points.length; i++) {
    const prev = points[i - 1];
    const curr = points[i];
    const prevVal = prev.doubleValue ?? prev.intValue ?? 0;
    const currVal = curr.doubleValue ?? curr.intValue ?? 0;
    if (currVal < prevVal) continue; // counter reset
    const dt = (new Date(curr.timestamp).getTime() - new Date(prev.timestamp).getTime()) / 1000;
    if (dt <= 0) continue;
    result.push([new Date(curr.timestamp).getTime(), (currVal - prevVal) / dt]);
  }
  return result;
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
