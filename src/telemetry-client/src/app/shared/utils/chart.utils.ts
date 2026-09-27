import type { ApexOptions } from 'ng-apexcharts';
import { getSeverityLabel, SEVERITY_COLORS } from '../../core/models/log.models';

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

/**
 * Minimal ApexCharts config for a KPI-card sparkline: no axes, no grid, no tooltip — just the
 * line shape. `series` values of `null` render as a gap rather than a dip to zero; callers must
 * pass `null` for buckets with no data (e.g. an empty time bucket), never `0`, or an idle period
 * reads as "duration crashed to 0ms" instead of "no data here".
 */
export function buildSparklineOptions(
  series: (number | null)[],
  isDark: boolean,
  color: string,
): ApexOptions {
  return {
    chart: {
      type: 'line',
      height: 40,
      // '%' width so the chart tracks its (shrinkable) CSS box instead of being pinned to a fixed
      // pixel size that would overflow a narrowed card. ApexCharts resolves a percentage width
      // against its own inner container element, so the `<apx-chart>` host must keep a definite
      // width — `.sparkline` in stat-card.component.ts supplies one via `flex: 0 1 100px`.
      // Height stays fixed: only the width should be fluid.
      width: '100%',
      sparkline: { enabled: true },
      background: 'transparent',
      animations: { enabled: false },
    },
    theme: { mode: isDark ? 'dark' : 'light' },
    series: [{ name: '', data: series }],
    colors: [color],
    stroke: { curve: 'smooth', width: 2 },
    // A series containing `null` makes ApexCharts emit one "virtual point" marker — a 0.1px-radius
    // circle pinned to the bottom-left of the plot, drawn with `alwaysDrawMarker`, so sparkline
    // mode's `markers.size: 0` does NOT suppress it. Its default 2px `#fff` stroke was the only
    // thing visible, showing up as a stray white dot in the corner of the cards whose series has
    // gaps (Avg Trace Duration, Error Rate). Zeroing the stroke hides it; sparkline mode draws no
    // other markers, so nothing else is affected.
    markers: { strokeWidth: 0 },
    tooltip: { enabled: false },
  };
}

/**
 * Geometry for a sparkline drawn as raw inline SVG, as an alternative to
 * {@link buildSparklineOptions}.
 *
 * ApexCharts is the right tool for one chart on a page; it is the wrong tool for one chart per
 * row of a grid, where each instance is a full chart engine rendering its own SVG for a line
 * with no axes, no grid, no tooltip and no interaction. A grid of fifty tenant cards pays that
 * fifty times over. This returns the handful of numbers a `<polyline>` needs instead.
 *
 * Same `null`-means-no-data contract as {@link buildSparklineOptions}: a null value breaks the
 * line rather than dipping it to zero, which is why the result is a list of segments.
 */
export interface SparklineShape {
  /** One `points` attribute per run of consecutive non-null values. */
  segments: string[];
  /**
   * Runs of length one. A `<polyline>` with a single point renders nothing at all, so an
   * isolated sample has to be drawn as a mark of its own or it silently disappears.
   */
  dots: { x: number; y: number }[];
}

/**
 * @param values Bucket values, `null` for "no data" (never 0 — see {@link SparklineShape}).
 * @param width  viewBox width. Pair with `preserveAspectRatio="none"` and a CSS width so the
 *               line stretches to the card; use `vector-effect="non-scaling-stroke"` on the
 *               shapes so the resulting non-uniform scale doesn't thicken the stroke sideways.
 */
export function buildSparklineShape(
  values: (number | null)[],
  width = 100,
  height = 24,
): SparklineShape {
  const empty: SparklineShape = { segments: [], dots: [] };
  if (values.length === 0) return empty;

  // Peak defines the top of the plot. An all-zero window is "nothing happened", not a flat line
  // pinned to the axis, so it draws nothing — same reasoning as the null contract.
  const max = Math.max(...values.map((v) => v ?? 0));
  if (max <= 0) return empty;

  // Half a stroke of headroom top and bottom, or the peak and the baseline get clipped.
  const pad = 2;
  const span = height - pad * 2;
  const step = values.length > 1 ? width / (values.length - 1) : 0;
  const xOf = (i: number) => (values.length > 1 ? i * step : width / 2);
  const yOf = (v: number) => height - pad - (v / max) * span;

  const segments: string[] = [];
  const dots: { x: number; y: number }[] = [];
  let run: string[] = [];

  const flush = () => {
    if (run.length > 1) segments.push(run.join(' '));
    else if (run.length === 1) {
      const [x, y] = run[0].split(',');
      dots.push({ x: Number(x), y: Number(y) });
    }
    run = [];
  };

  values.forEach((v, i) => {
    if (v == null) { flush(); return; }
    run.push(`${xOf(i).toFixed(2)},${yOf(v).toFixed(2)}`);
  });
  flush();

  return { segments, dots };
}

export interface TimeBucket {
  timestamp: Date;
  count: number;
  errorCount: number;
  sumDurationMs: number;
  /** Duration percentiles (ms) for this bucket's traces. 0 when count === 0 — treat as no data. */
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
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
  items: { timeUnixNano?: number; severityNumber?: number }[],
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

// ===========================================================================
// HISTOGRAM BUCKETS — percentiles
// ===========================================================================

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

/**
 * Canonical percentile palette: a green → orange → red severity gradient shared by the dashboard's
 * "Latency Over Time" chart and the histogram percentile chart on metric detail, so the same
 * percentile always reads the same color. `Max` sits outside the gradient in purple — it's
 * worst-case context, not a fourth severity tier.
 */
export const PERCENTILE_COLORS: Record<string, string> = {
  p50: '#4caf50', p95: '#ff9800', p99: '#f44336', Max: '#9c27b0',
};

export function formatDuration(ms: number): string {
  return (ms < 0 ? '-' : '') + formatDurationMs(Math.abs(ms));
}

/** Auto-scaled µs/ms/s label for a non-negative duration already expressed in milliseconds. */
function formatDurationMs(ms: number): string {
  if (ms < 1) return `${(ms * 1000).toFixed(0)}µs`;
  if (ms < 1000) return `${ms.toFixed(1)}ms`;
  return `${(ms / 1000).toFixed(2)}s`;
}

/** Auto-scaled B/KB/MB/GB/TB label for a non-negative byte count (binary/1024-based steps). */
function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = bytes;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${i === 0 ? v.toFixed(0) : v.toFixed(2)}${units[i]}`;
}

/** Milliseconds-per-unit for recognized OTLP/UCUM time units. */
const TIME_UNIT_TO_MS: Record<string, number> = {
  s: 1000, ms: 1, us: 0.001, 'µs': 0.001, ns: 0.000001,
};

/** Bytes-per-unit for recognized OTLP/UCUM byte units (binary/1024-based). */
const BYTE_UNIT_TO_BYTES: Record<string, number> = {
  By: 1, KiBy: 1024, MiBy: 1024 ** 2, GiBy: 1024 ** 3, TiBy: 1024 ** 4,
};

/**
 * Formats a raw metric value using its OTLP/UCUM `unit` string: recognized time units
 * auto-scale through µs/ms/s (via {@link formatDurationMs}), recognized byte units
 * auto-scale through B/KB/MB/GB/TB, `%` and dimensionless (`1`) get their conventional
 * bare/suffixed form, and any other unit (or no unit) falls back to a plain rounded number
 * with the raw unit string appended. Used for histogram/exp-histogram bucket bounds and
 * axis/tooltip labels so a chart's numbers read in a unit-appropriate scale instead of the
 * stored magnitude verbatim.
 */
export function formatUnitValue(value: number, unit?: string | null): string {
  const u = unit ?? '';
  const sign = value < 0 ? '-' : '';
  const abs = Math.abs(value);

  if (u in TIME_UNIT_TO_MS) return sign + formatDurationMs(abs * TIME_UNIT_TO_MS[u]);
  if (u in BYTE_UNIT_TO_BYTES) return sign + formatBytes(abs * BYTE_UNIT_TO_BYTES[u]);
  if (u === '%') return `${value}%`;
  if (u === '1' || u === '') return plainNumber(value);
  return `${plainNumber(value)} ${u}`;
}

/** Bare-number formatting shared by the unit-less fallback paths: fixed precision for typical
 *  magnitudes, significant-digit precision (no scientific notation surprises) for very small/large. */
function plainNumber(v: number): string {
  const abs = Math.abs(v);
  if (abs === 0) return '0';
  if (abs >= 1000 || abs < 0.01) return Number(v.toPrecision(3)).toString();
  return Number(v.toFixed(3)).toString();
}

export function parseDotnetTimespan(ts: string): number {
  // Handles "00:00:01.234" (h:m:s[.fff]) and "1.00:00:00[.fff]" (d.h:m:s[.fff], .NET's format for
  // a TimeSpan of a day or more). The two are told apart by a '.' in the *hours* slot — the first
  // ':'-delimited part — not the seconds slot, where a '.' is just a fractional-second separator.
  // Previously mishandled: "1.00:00:00".split(':')[0] is "1.00", and parseFloat read that whole
  // "day.hour" pair as 1.00 *hours*, undercounting a multi-day duration by ~24x.
  const parts = ts.split(':');
  if (parts.length !== 3) return 0;

  let days = 0;
  let hoursPart = parts[0];
  const dotIdx = hoursPart.indexOf('.');
  if (dotIdx >= 0) {
    days = parseFloat(hoursPart.slice(0, dotIdx));
    hoursPart = hoursPart.slice(dotIdx + 1);
  }

  const h = parseFloat(hoursPart);
  const m = parseFloat(parts[1]);
  const s = parseFloat(parts[2]);
  return (days * 86400 + h * 3600 + m * 60 + s) * 1000;
}
