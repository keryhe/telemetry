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
    xaxis: { categories: buckets.map((b) => b.time.getTime()), type: 'datetime', labels: { datetimeUTC: false } },
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

// ===========================================================================
// HISTOGRAM BUCKETS — percentiles + heatmap
// ===========================================================================

/** Explicit-bucket representation: counts has one more entry than bounds (the +Inf overflow). */
export interface ExplicitBuckets {
  counts: number[];
  bounds: number[];
}

/**
 * Normalizes a histogram or exponential-histogram point into explicit buckets so the same
 * quantile/heatmap math applies to both. Returns null if the point carries no bucket data.
 *
 * For exponential histograms: base = 2^(2^-scale), positive bucket i spans
 * (base^(offset+i), base^(offset+i+1)]. We prepend the zero bucket, giving
 * counts = [zeroCount, ...positiveBucketCounts] and bounds[k] = base^(offset+k).
 */
export function toExplicitBuckets(point: MetricDataPoint): ExplicitBuckets | null {
  if (point.bucketCounts && point.bucketCounts.length) {
    return { counts: point.bucketCounts, bounds: point.bucketBounds ?? [] };
  }
  if (point.positiveBucketCounts && point.positiveBucketCounts.length && point.scale != null) {
    const offset = point.positiveOffset ?? 0;
    const log2Base = Math.pow(2, -point.scale);
    const pos = point.positiveBucketCounts;
    const counts = [point.zeroCount ?? 0, ...pos];
    const bounds: number[] = [];
    for (let k = 0; k < pos.length; k++) bounds.push(Math.pow(2, log2Base * (offset + k)));
    return { counts, bounds };
  }
  return null;
}

/**
 * Prometheus-style quantile from explicit histogram buckets, with linear interpolation
 * inside the matched bucket. `counts` are per-bucket (not cumulative); `bounds` are the
 * upper bounds of every bucket except the final +Inf overflow. Returns NaN for empty/zero
 * histograms. The first bucket's lower bound is treated as 0; a rank landing in the overflow
 * bucket clamps to its (finite) lower bound.
 */
export function histogramQuantile(counts: number[], bounds: number[], q: number): number {
  if (!counts || counts.length === 0) return NaN;
  const total = counts.reduce((a, b) => a + b, 0);
  if (total === 0) return NaN;
  const clamped = Math.min(Math.max(q, 0), 1);
  const rank = clamped * total;

  let cum = 0;
  for (let i = 0; i < counts.length; i++) {
    const c = counts[i];
    if (cum + c >= rank) {
      const lower = i === 0 ? 0 : bounds[i - 1];
      const upper = i < bounds.length ? bounds[i] : Infinity;
      if (!isFinite(upper)) return lower;            // overflow bucket: clamp to lower bound
      if (c === 0) return upper;
      return lower + (upper - lower) * ((rank - cum) / c);
    }
    cum += c;
  }
  return bounds.length ? bounds[bounds.length - 1] : NaN;
}

function fmtBound(v: number): string {
  if (!isFinite(v)) return '∞';
  if (v === 0) return '0';
  const abs = Math.abs(v);
  if (abs >= 1000 || abs < 0.01) return v.toPrecision(3);
  return Number(v.toFixed(3)).toString();
}

/** Human-readable range label per bucket, e.g. "< 5", "5 – 10", "≥ 100". */
function bucketLabels(counts: number[], bounds: number[]): string[] {
  const labels: string[] = [];
  for (let i = 0; i < counts.length; i++) {
    if (i === 0) labels.push(`< ${fmtBound(bounds[0] ?? Infinity)}`);
    else if (i < bounds.length) labels.push(`${fmtBound(bounds[i - 1])} – ${fmtBound(bounds[i])}`);
    else labels.push(`≥ ${fmtBound(bounds[bounds.length - 1])}`);
  }
  return labels;
}

/**
 * Builds an ApexCharts heatmap (X = time, Y = bucket range, color = count) from histogram or
 * exponential-histogram points. Bucket structure is taken from the first point that has
 * buckets; points with a mismatched bucket count are skipped. Returns null if no point has
 * usable bucket data.
 */
export function buildHistogramHeatmap(points: MetricDataPoint[], isDark: boolean): ApexOptions | null {
  const ordered = [...points].sort(
    (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
  );
  const template = ordered.map(toExplicitBuckets).find((b) => b != null) as ExplicitBuckets | undefined;
  if (!template) return null;

  const bucketCount = template.counts.length;
  const labels = bucketLabels(template.counts, template.bounds);

  // One heatmap row per bucket; ApexCharts renders the first series at the bottom.
  const series = labels.map((label) => ({ name: label, data: [] as { x: number; y: number }[] }));
  let maxCount = 0;
  for (const p of ordered) {
    const b = toExplicitBuckets(p);
    if (!b || b.counts.length !== bucketCount) continue;
    const x = new Date(p.timestamp).getTime();
    for (let i = 0; i < bucketCount; i++) {
      const y = b.counts[i];
      series[i].data.push({ x, y });
      if (y > maxCount) maxCount = y;
    }
  }
  if (series.every((s) => s.data.length === 0)) return null;

  return {
    chart: { type: 'heatmap', height: 320, toolbar: { show: false }, background: 'transparent' },
    theme: { mode: isDark ? 'dark' : 'light' },
    series,
    xaxis: { type: 'datetime', labels: { datetimeUTC: false } },
    dataLabels: { enabled: false },
    legend: { show: false },
    plotOptions: {
      heatmap: {
        shadeIntensity: 0.5,
        colorScale: {
          ranges: [
            { from: 0, to: 0, color: '#263238', name: '0' },
            { from: 0.001, to: maxCount * 0.25, color: '#90CAF9', name: 'Low' },
            { from: maxCount * 0.25, to: maxCount * 0.6, color: '#1976D2', name: 'Medium' },
            { from: maxCount * 0.6, to: Math.max(maxCount, 1), color: '#0D47A1', name: 'High' },
          ],
        },
      },
    },
  };
}

// ===========================================================================
// CROSS-SERIES ALIGNMENT + AGGREGATION
// ===========================================================================

export type AggregateFn = 'sum' | 'avg' | 'min' | 'max';

/** Raw value of a data point (gauge/sum), independent of histogram fields. */
function pointValue(p: MetricDataPoint): number {
  return p.doubleValue ?? p.intValue ?? 0;
}

/** A series' [time, value] points, as either per-second rate (counters) or raw values. */
function seriesToData(points: MetricDataPoint[], asRate: boolean): [number, number][] {
  return asRate
    ? computeRateSeries(points)
    : points.map((p) => [new Date(p.timestamp).getTime(), pointValue(p)] as [number, number]);
}

/** Evenly-spaced epoch grid of `bucketCount` steps across [start, end] (bucketCount+1 points). */
function makeGrid(start: Date, end: Date, bucketCount: number): number[] {
  const s = start.getTime();
  const span = end.getTime() - s;
  const step = span / bucketCount;
  return Array.from({ length: bucketCount + 1 }, (_, i) => s + i * step);
}

/**
 * Resamples a [t, v][] series onto `grid` by last-value carry-forward: each grid point takes
 * the most recent sample at or before it. Grid points preceding the first sample are null.
 */
function resampleToGrid(data: [number, number][], grid: number[]): (number | null)[] {
  const out: (number | null)[] = new Array(grid.length).fill(null);
  if (!data.length) return out;
  const sorted = [...data].sort((a, b) => a[0] - b[0]);
  let j = 0;
  let last: number | null = null;
  for (let i = 0; i < grid.length; i++) {
    while (j < sorted.length && sorted[j][0] <= grid[i]) { last = sorted[j][1]; j++; }
    out[i] = last;
  }
  return out;
}

/**
 * Combines multiple series into one line with `agg` (sum/avg/min/max), after aligning them
 * onto a common time grid. For counters pass `asRate = true` so the per-series **rate** is
 * computed first and then aggregated (Grafana's `sum(rate(...))`); aggregating raw cumulative
 * values and diffing afterward is incorrect. Grid points where no series has data are skipped.
 */
export function aggregateSeries(
  seriesList: { points: MetricDataPoint[] }[],
  start: Date,
  end: Date,
  agg: AggregateFn,
  asRate: boolean,
  bucketCount = 60,
): [number, number][] {
  if (!seriesList.length) return [];
  const grid = makeGrid(start, end, bucketCount);
  const resampled = seriesList.map((s) => resampleToGrid(seriesToData(s.points, asRate), grid));

  const result: [number, number][] = [];
  for (let i = 0; i < grid.length; i++) {
    const vals = resampled.map((r) => r[i]).filter((v): v is number => v != null);
    if (!vals.length) continue;
    let v: number;
    switch (agg) {
      case 'sum': v = vals.reduce((a, b) => a + b, 0); break;
      case 'avg': v = vals.reduce((a, b) => a + b, 0) / vals.length; break;
      case 'min': v = Math.min(...vals); break;
      case 'max': v = Math.max(...vals); break;
    }
    result.push([grid[i], v]);
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
