import type { ApexOptions } from 'ng-apexcharts';
import { getSeverityLabel, SEVERITY_COLORS } from '../../core/models/log.models';
import { AggregationTemporality, MetricDataPoint, MetricType } from '../../core/models/metric.models';

/** Shared grid / separator line color: light gray in light mode, darker gray in dark mode. */
function gridLineColor(isDark: boolean): string {
  return isDark ? '#3a3b40' : '#d8d8d8';
}

/** Shared, theme-aware ApexCharts grid config: muted low-contrast lines in both themes. */
export function chartGrid(isDark: boolean): ApexOptions['grid'] {
  return { borderColor: gridLineColor(isDark), strokeDashArray: 0 };
}

/**
 * Chart config for a datetime x-axis: disables mouse-wheel zoom and turns a horizontal
 * drag-select into a time-range update (matching the Traces/Logs charts). Spread into an
 * ApexCharts `chart: { ... }`. `onSelect` receives the dragged [start, end].
 */
export function timeRangeZoom(
  onSelect: (start: Date, end: Date) => void,
): Pick<NonNullable<ApexOptions['chart']>, 'zoom' | 'events'> {
  return {
    zoom: { enabled: true, type: 'x', allowMouseWheelZoom: false },
    events: {
      zoomed: (_ctx, opts) => {
        const min = opts?.xaxis?.min;
        const max = opts?.xaxis?.max;
        if (min != null && max != null) onSelect(new Date(min), new Date(max));
      },
    },
  };
}

export interface TimeBucket {
  timestamp: Date;
  count: number;
  errorCount: number;
  sumDurationMs: number;
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
  if (abs >= 1000 || abs < 0.01) return Number(v.toPrecision(3)).toString();
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
  return renderHeatmap(series, maxCount, isDark);
}

/** Shared ApexCharts heatmap config for the bucket-distribution charts. */
function renderHeatmap(
  series: { name: string; data: { x: number; y: number }[] }[],
  maxCount: number,
  isDark: boolean,
): ApexOptions {
  // Scale height with row count so rows never collapse sub-pixel; clamp to a sensible band.
  const height = Math.min(420, Math.max(180, series.length * 18));
  // Empty (zero-count) cells blend with the mat-card surface per theme; thin separators just off
  // that tone keep the grid legible without a heavy dark background in light mode.
  const zeroColor = isDark ? '#26272b' : '#f4f4f5';
  const separatorColor = gridLineColor(isDark);
  return {
    chart: { type: 'heatmap', height, toolbar: { show: false }, background: 'transparent' },
    theme: { mode: isDark ? 'dark' : 'light' },
    series,
    xaxis: { type: 'datetime', labels: { datetimeUTC: false } },
    dataLabels: { enabled: false },
    legend: { show: false },
    stroke: { width: 0.5, colors: [separatorColor] },
    grid: chartGrid(isDark),
    plotOptions: {
      heatmap: {
        shadeIntensity: 0.5,
        colorScale: {
          ranges: [
            { from: 0, to: 0, color: zeroColor, name: '0' },
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

/**
 * Shared categorical palette for multi-slice charts (donut share, future grouped views). Slices
 * are assigned by index modulo the length, matching the per-service coloring used elsewhere.
 */
export const CATEGORICAL_COLORS = [
  '#1976d2', '#f57c00', '#388e3c', '#7b1fa2', '#00838f', '#5d4037', '#558b2f', '#4527a0',
];

/**
 * Donut chart of share-of-total across slices (e.g. one slice per service/label set). Slices
 * arrive pre-reduced to a single value each; zero/negative values are dropped so the ring only
 * shows real contributions. Returns null if nothing positive remains.
 */
export function buildShareDonut(
  slices: { name: string; value: number }[],
  isDark: boolean,
): ApexOptions | null {
  const positive = slices.filter((s) => s.value > 0);
  if (!positive.length) return null;
  return {
    chart: { type: 'donut', height: 320, toolbar: { show: false }, background: 'transparent' },
    theme: { mode: isDark ? 'dark' : 'light' },
    series: positive.map((s) => s.value),
    labels: positive.map((s) => s.name),
    colors: positive.map((_, i) => CATEGORICAL_COLORS[i % CATEGORICAL_COLORS.length]),
    dataLabels: { enabled: true, formatter: (val: number) => `${val.toFixed(1)}%` },
    legend: { position: 'right' },
    stroke: { width: 0 },
    grid: chartGrid(isDark),
  };
}

/**
 * Radial gauge (single value against a max). `value` and `max` are in the metric's own units;
 * the ring shows the percentage while the center label shows the real value + optional unit.
 */
export function buildRadialGauge(
  value: number,
  max: number,
  label: string,
  isDark: boolean,
  unit = '',
): ApexOptions {
  const pct = max > 0 ? Math.min(100, Math.max(0, (value / max) * 100)) : 0;
  return {
    chart: { type: 'radialBar', height: 320, toolbar: { show: false }, background: 'transparent' },
    theme: { mode: isDark ? 'dark' : 'light' },
    series: [Number(pct.toFixed(1))],
    labels: [label],
    colors: [CATEGORICAL_COLORS[0]],
    plotOptions: {
      radialBar: {
        hollow: { size: '60%' },
        dataLabels: {
          name: { offsetY: -8 },
          value: {
            offsetY: 4,
            formatter: () => `${Number(value.toFixed(2))}${unit}`,
          },
        },
      },
    },
    grid: chartGrid(isDark),
  };
}

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

/** One time window of an explicit-bucket histogram, aggregated across all series. */
export interface HistogramWindow {
  /** Window start (epoch ms). */
  edge: number;
  /** Summed per-interval bucket counts, aligned to the shared bounds. */
  counts: number[];
  /** Σ counts (total observations in the window). */
  total: number;
  /** Σ per-interval sum-delta (for computing the mean). */
  sum: number;
  /** Min of contributing point minimums (envelope), null if none. */
  min: number | null;
  /** Max of contributing point maximums (envelope), null if none. */
  max: number | null;
}

/**
 * Shared windowing core for explicit-bucket histograms. Over a common {@link makeGrid} grid it sums,
 * per window and across all series, the *per-interval* bucket-count vector, the sum-delta, and a
 * min/max envelope — the single source the percentile, throughput, mean, min/max and heatmap views
 * all derive from, so they stay self-consistent.
 *
 * Cumulative series are diffed between consecutive scrapes (reset-safe, mirroring
 * {@link computeRateSeries}); delta series contribute their own values directly. Points whose bucket
 * schema doesn't match the reference bounds (rare) are skipped.
 */
export function aggregateHistogramWindows(
  seriesList: { points: MetricDataPoint[] }[],
  start: Date,
  end: Date,
  bucketCount = 60,
): { bounds: number[]; windows: HistogramWindow[] } {
  const grid = makeGrid(start, end, bucketCount);

  // Reference bucket schema (bounds + counts length) from the first point that carries buckets.
  let refBounds: number[] | null = null;
  let refLen = 0;
  for (const s of seriesList) {
    for (const p of s.points) {
      const b = toExplicitBuckets(p);
      if (b) { refBounds = b.bounds; refLen = b.counts.length; break; }
    }
    if (refBounds) break;
  }
  if (!refBounds) return { bounds: [], windows: [] };

  const counts: number[][] = Array.from({ length: grid.length }, () => new Array(refLen).fill(0));
  const sums: number[] = new Array(grid.length).fill(0);
  const mins: (number | null)[] = new Array(grid.length).fill(null);
  const maxs: (number | null)[] = new Array(grid.length).fill(null);

  const windowIndex = (t: number): number => {
    if (t <= grid[0]) return 0;
    if (t >= grid[grid.length - 1]) return grid.length - 1;
    const step = (grid[grid.length - 1] - grid[0]) / bucketCount;
    return Math.min(grid.length - 1, Math.max(0, Math.floor((t - grid[0]) / step)));
  };

  const add = (t: number, bucket: number[], sumDelta: number, min: number | null, max: number | null): void => {
    if (bucket.length !== refLen) return; // mismatched schema
    const idx = windowIndex(t);
    const c = counts[idx];
    for (let i = 0; i < refLen; i++) c[i] += bucket[i];
    sums[idx] += sumDelta;
    if (min != null) mins[idx] = mins[idx] == null ? min : Math.min(mins[idx]!, min);
    if (max != null) maxs[idx] = maxs[idx] == null ? max : Math.max(maxs[idx]!, max);
  };

  for (const s of seriesList) {
    const pts = [...s.points]
      .filter((p) => toExplicitBuckets(p) != null)
      .sort((a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime());
    if (!pts.length) continue;

    const isCumulative = pts[0].aggregationTemporality === AggregationTemporality.Cumulative;

    if (!isCumulative) {
      for (const p of pts) {
        const b = toExplicitBuckets(p)!;
        add(new Date(p.timestamp).getTime(), b.counts, p.sum ?? 0, p.min ?? null, p.max ?? null);
      }
      continue;
    }

    // Cumulative: per-interval delta between consecutive scrapes, clamping resets to 0.
    for (let i = 1; i < pts.length; i++) {
      const prev = toExplicitBuckets(pts[i - 1])!;
      const curr = toExplicitBuckets(pts[i])!;
      if (prev.counts.length !== refLen || curr.counts.length !== refLen) continue;
      const delta = new Array(refLen);
      for (let k = 0; k < refLen; k++) delta[k] = Math.max(0, curr.counts[k] - prev.counts[k]);
      const sumDelta = Math.max(0, (pts[i].sum ?? 0) - (pts[i - 1].sum ?? 0));
      add(new Date(pts[i].timestamp).getTime(), delta, sumDelta, pts[i].min ?? null, pts[i].max ?? null);
    }
  }

  const windows: HistogramWindow[] = grid.map((edge, i) => ({
    edge,
    counts: counts[i],
    total: counts[i].reduce((a, b) => a + b, 0),
    sum: sums[i],
    min: mins[i],
    max: maxs[i],
  }));

  return { bounds: refBounds, windows };
}

/**
 * True aggregate percentile lines for explicit-bucket histograms, computed the Prometheus way:
 * `histogram_quantile(q, sum by (le) (rate(bucket[window])))` — via {@link aggregateHistogramWindows},
 * running {@link histogramQuantile} on each window's summed buckets. Empty windows are omitted.
 */
export function aggregateHistogramQuantiles(
  seriesList: { points: MetricDataPoint[] }[],
  start: Date,
  end: Date,
  quantiles: { q: number; label: string }[],
  bucketCount = 60,
): { q: number; label: string; data: [number, number][] }[] {
  const { bounds, windows } = aggregateHistogramWindows(seriesList, start, end, bucketCount);
  if (!windows.length) return [];

  return quantiles.map(({ q, label }) => ({
    q,
    label,
    data: windows
      .map((w) => {
        if (w.total === 0) return null;
        const v = histogramQuantile(w.counts, bounds, q);
        return Number.isFinite(v) ? ([w.edge, v] as [number, number]) : null;
      })
      .filter((d): d is [number, number] => d != null),
  })).filter((s) => s.data.length > 0);
}

/** Upper bound on heatmap Y-rows; finer bucket schemas (exp histograms) are merged down to this. */
const MAX_HEATMAP_ROWS = 24;

/**
 * Groups an over-fine bucket schema into at most `maxRows` display bins by merging adjacent buckets.
 * Returns the group ranges `[start, end)` into the original counts, plus the merged bounds (upper
 * bound of each group except the final overflow group). Counts are summed per group by the caller.
 * Schemas already within `maxRows` yield one group per bucket (identity), so explicit histograms are
 * unchanged.
 *
 * Bucket semantics match {@link bucketLabels}/{@link histogramQuantile}: `counts.length ===
 * bounds.length + 1`; bucket i covers (bounds[i-1], bounds[i]], the first is `< bounds[0]` and the
 * last is the `≥` overflow. The merged output preserves that invariant.
 */
function planBucketGroups(
  refLen: number,
  bounds: number[],
  maxRows: number,
): { groups: [number, number][]; bounds: number[] } {
  if (refLen <= maxRows) {
    return { groups: Array.from({ length: refLen }, (_, i) => [i, i + 1] as [number, number]), bounds };
  }
  const g = Math.ceil(refLen / maxRows);
  const groups: [number, number][] = [];
  const merged: number[] = [];
  for (let start = 0; start < refLen; start += g) {
    const end = Math.min(start + g, refLen);
    groups.push([start, end]);
    // Every group except the final overflow contributes its last bucket's upper bound.
    if (end < refLen) merged.push(bounds[end - 1]);
  }
  return { groups, bounds: merged };
}

/**
 * Bucket-distribution heatmap built from pre-aggregated {@link HistogramWindow}s (per-window delta
 * counts → a true histogram-over-time, not cumulative growth). Over-fine schemas (exponential
 * histograms carry ~100+ buckets) are merged down to {@link MAX_HEATMAP_ROWS} legible rows via
 * {@link planBucketGroups}; explicit histograms already under the cap are unchanged. Returns null if
 * there is no data.
 */
export function buildHistogramHeatmapFromWindows(
  bounds: number[],
  windows: HistogramWindow[],
  isDark: boolean,
): ApexOptions | null {
  if (!windows.length || !windows[0].counts.length) return null;
  const refLen = windows[0].counts.length;
  const { groups, bounds: rowBounds } = planBucketGroups(refLen, bounds, MAX_HEATMAP_ROWS);
  const labels = bucketLabels(new Array(groups.length), rowBounds);

  const series = labels.map((label) => ({ name: label, data: [] as { x: number; y: number }[] }));
  let maxCount = 0;
  for (const w of windows) {
    for (let r = 0; r < groups.length; r++) {
      const [start, end] = groups[r];
      let y = 0;
      for (let i = start; i < end; i++) y += w.counts[i];
      series[r].data.push({ x: w.edge, y });
      if (y > maxCount) maxCount = y;
    }
  }
  if (maxCount === 0) return null;
  return renderHeatmap(series, maxCount, isDark);
}

/**
 * Bar chart of a single {@link HistogramWindow}'s bucket distribution: X = bucket range,
 * Y = observation count. Over-fine schemas (exponential histograms carry ~100+ buckets) are
 * merged down to at most {@link MAX_HEATMAP_ROWS} bars via {@link planBucketGroups}, summing
 * counts per group; explicit histograms under the cap are unchanged. Returns null for an
 * empty/zero window.
 */
export function buildHistogramBarFromWindow(
  bounds: number[],
  window: HistogramWindow,
  isDark: boolean,
): ApexOptions | null {
  if (!window.counts.length || window.total === 0) return null;
  const { groups, bounds: barBounds } = planBucketGroups(window.counts.length, bounds, MAX_HEATMAP_ROWS);
  const counts = groups.map(([start, end]) => {
    let sum = 0;
    for (let i = start; i < end; i++) sum += window.counts[i];
    return sum;
  });
  const labels = bucketLabels(new Array(groups.length), barBounds);

  return {
    chart: {
      type: 'bar', height: 220, toolbar: { show: false }, background: 'transparent',
      zoom: { allowMouseWheelZoom: false },
    },
    theme: { mode: isDark ? 'dark' : 'light' },
    series: [{ name: 'Count', data: counts }],
    xaxis: { categories: labels, labels: { rotate: -45, hideOverlappingLabels: true } },
    dataLabels: { enabled: false },
    legend: { show: false },
    grid: chartGrid(isDark),
    plotOptions: { bar: { columnWidth: '80%' } },
  };
}

/**
 * Bar chart of the bucket distribution summed across every window in the selected range — the
 * whole-range "overall shape" view (vs. {@link buildHistogramBarFromWindow}'s single window). Sums
 * the per-window `counts` vectors element-wise into one aggregate {@link HistogramWindow}, then
 * reuses the single-window builder so exp-histogram bucket-merging and styling stay in one place.
 * Returns null if there is no window with observations.
 */
export function buildHistogramBarFromWindows(
  bounds: number[],
  windows: HistogramWindow[],
  isDark: boolean,
): ApexOptions | null {
  const refLen = windows.find((w) => w.counts.length)?.counts.length ?? 0;
  if (!refLen) return null;

  const counts = new Array(refLen).fill(0);
  let total = 0;
  for (const w of windows) {
    if (w.counts.length !== refLen) continue;
    for (let i = 0; i < refLen; i++) counts[i] += w.counts[i];
    total += w.total;
  }

  const aggregate: HistogramWindow = { edge: windows[0]?.edge ?? 0, counts, total, sum: 0, min: null, max: null };
  return buildHistogramBarFromWindow(bounds, aggregate, isDark);
}

/**
 * Normalizes exponential-histogram series onto one shared explicit-bucket schema so the standard
 * histogram windowing/quantile math applies. Every point is downscaled to the coarsest `scale` present
 * (standard OTLP base-2 downscaling — merging 2^delta adjacent buckets) and re-expressed on a common
 * exponent-index grid; the synthetic points carry explicit `bucketCounts`/`bucketBounds`, so downstream
 * code treats them exactly like explicit histograms. Negative buckets are ignored (durations are
 * non-negative). Non-exponential points pass through unchanged.
 */
export function normalizeExpHistogramSeries(
  seriesList: { points: MetricDataPoint[] }[],
): { points: MetricDataPoint[] }[] {
  const isExp = (p: MetricDataPoint) =>
    p.positiveBucketCounts != null && p.positiveBucketCounts.length > 0 && p.scale != null;

  // Coarsest (smallest) scale across all points = widest buckets everything can merge into.
  let targetScale = Infinity;
  for (const s of seriesList) for (const p of s.points) if (isExp(p)) targetScale = Math.min(targetScale, p.scale!);
  if (!isFinite(targetScale)) return seriesList;

  // Downscale a point's positive buckets to targetScale, keyed by absolute exponent index.
  const downscale = (p: MetricDataPoint): Map<number, number> => {
    const m = new Map<number, number>();
    const factor = Math.pow(2, p.scale! - targetScale); // >= 1
    const offset = p.positiveOffset ?? 0;
    const pos = p.positiveBucketCounts!;
    for (let k = 0; k < pos.length; k++) {
      if (!pos[k]) continue;
      const jPrime = Math.floor((offset + k) / factor);
      m.set(jPrime, (m.get(jPrime) ?? 0) + pos[k]);
    }
    return m;
  };

  let minIdx = Infinity;
  let maxIdx = -Infinity;
  const perPoint = new Map<MetricDataPoint, Map<number, number>>();
  for (const s of seriesList) for (const p of s.points) {
    if (!isExp(p)) continue;
    const m = downscale(p);
    perPoint.set(p, m);
    for (const j of m.keys()) { minIdx = Math.min(minIdx, j); maxIdx = Math.max(maxIdx, j); }
  }

  const n = isFinite(minIdx) && isFinite(maxIdx) ? maxIdx - minIdx + 1 : 0;
  const log2Base = Math.pow(2, -targetScale);
  const bounds: number[] = [];
  for (let m = 0; m < n; m++) bounds.push(Math.pow(2, log2Base * (minIdx + m)));

  return seriesList.map((s) => ({
    points: s.points.map((p) => {
      if (!isExp(p)) return p;
      const map = perPoint.get(p)!;
      const counts = new Array(n + 1).fill(0);
      counts[0] = p.zeroCount ?? 0;
      for (const [j, c] of map) counts[j - minIdx + 1] = c;
      return { ...p, bucketCounts: counts, bucketBounds: bounds } as MetricDataPoint;
    }),
  }));
}

/**
 * Windowed aggregate of the *additive* summary fields (count & sum) across all series, for the
 * Throughput/Mean view. Returns bucket-less {@link HistogramWindow}s so it reuses the same chart
 * builder. Summaries are implicitly cumulative → per-series delta between consecutive points.
 */
export function aggregateSummaryWindows(
  seriesList: { points: MetricDataPoint[] }[],
  start: Date,
  end: Date,
  bucketCount = 60,
): HistogramWindow[] {
  const grid = makeGrid(start, end, bucketCount);
  const totals: number[] = new Array(grid.length).fill(0);
  const sums: number[] = new Array(grid.length).fill(0);

  const windowIndex = (t: number): number => {
    if (t <= grid[0]) return 0;
    if (t >= grid[grid.length - 1]) return grid.length - 1;
    const step = (grid[grid.length - 1] - grid[0]) / bucketCount;
    return Math.min(grid.length - 1, Math.max(0, Math.floor((t - grid[0]) / step)));
  };

  for (const s of seriesList) {
    const pts = [...s.points]
      .filter((p) => p.count != null)
      .sort((a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime());
    for (let i = 1; i < pts.length; i++) {
      const idx = windowIndex(new Date(pts[i].timestamp).getTime());
      totals[idx] += Math.max(0, (pts[i].count ?? 0) - (pts[i - 1].count ?? 0));
      sums[idx] += Math.max(0, (pts[i].sum ?? 0) - (pts[i - 1].sum ?? 0));
    }
  }

  return grid.map((edge, i) => ({ edge, counts: [], total: totals[i], sum: sums[i], min: null, max: null }));
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
