import type { ApexOptions } from 'ng-apexcharts';
import { getSeverityLabel, SEVERITY_COLORS } from '../../core/models/log.models';
import { MetricBucketPoint, MetricType } from '../../core/models/metric.models';

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

/** Auto-scaled B/KiB/MiB/GiB/TiB label for a non-negative byte count (binary/1024-based steps). */
function formatBinaryBytes(bytes: number): string {
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let v = bytes;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${i === 0 ? v.toFixed(0) : v.toFixed(2)}${units[i]}`;
}

/** Auto-scaled B/kB/MB/GB/TB label for a non-negative byte count (SI/1000-based steps). */
function formatDecimalBytes(bytes: number): string {
  const units = ['B', 'kB', 'MB', 'GB', 'TB'];
  let v = bytes;
  let i = 0;
  while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
  return `${i === 0 ? v.toFixed(0) : v.toFixed(2)}${units[i]}`;
}

/** Auto-scaled bit/kbit/Mbit/Gbit label for a non-negative bit count (SI/1000-based steps). */
function formatBits(bits: number): string {
  const units = ['bit', 'kbit', 'Mbit', 'Gbit', 'Tbit'];
  let v = bits;
  let i = 0;
  while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
  return `${i === 0 ? v.toFixed(0) : v.toFixed(2)} ${units[i]}`;
}

/** Time label that keeps scaling above seconds (min / h / d). Used only when the metric's own unit is
 *  already minutes, hours or days; a unit of seconds or below stays capped at seconds (see
 *  {@link formatDurationMs}), so latency charts read as they always have. */
function formatLongDurationMs(ms: number): string {
  if (ms < 60_000) return formatDurationMs(ms);
  const min = ms / 60_000;
  if (min < 60) return `${min.toFixed(1)}min`;
  const h = min / 60;
  if (h < 24) return `${h.toFixed(1)}h`;
  return `${(h / 24).toFixed(1)}d`;
}

type UnitKind =
  | 'none' | 'time' | 'longTime' | 'bytesBinary' | 'bytesDecimal' | 'bits'
  | 'percent' | 'hz' | 'cel' | 'unknown';

/** A recognised unit: what family it belongs to and how many of the family's base unit (ms, bytes,
 *  bits) one of it is. */
interface UnitDef { kind: UnitKind; factor: number; }

/** Exact (case-sensitive) spellings. UCUM is case-sensitive: `MBy` and `mBy` differ by a factor of a
 *  billion, so prefixed spellings are only ever matched here, never through the alias table. */
const UNIT_TABLE: Record<string, UnitDef> = {
  // time (base unit: milliseconds)
  ns: { kind: 'time', factor: 0.000001 }, us: { kind: 'time', factor: 0.001 }, 'µs': { kind: 'time', factor: 0.001 },
  ms: { kind: 'time', factor: 1 }, s: { kind: 'time', factor: 1000 },
  min: { kind: 'longTime', factor: 60_000 }, h: { kind: 'longTime', factor: 3_600_000 }, d: { kind: 'longTime', factor: 86_400_000 },
  // bytes, binary (base unit: bytes)
  By: { kind: 'bytesBinary', factor: 1 }, KiBy: { kind: 'bytesBinary', factor: 1024 },
  MiBy: { kind: 'bytesBinary', factor: 1024 ** 2 }, GiBy: { kind: 'bytesBinary', factor: 1024 ** 3 },
  TiBy: { kind: 'bytesBinary', factor: 1024 ** 4 },
  // bytes, decimal
  kBy: { kind: 'bytesDecimal', factor: 1000 }, KBy: { kind: 'bytesDecimal', factor: 1000 },
  MBy: { kind: 'bytesDecimal', factor: 1000 ** 2 }, GBy: { kind: 'bytesDecimal', factor: 1000 ** 3 },
  TBy: { kind: 'bytesDecimal', factor: 1000 ** 4 },
  // bits (base unit: bits)
  bit: { kind: 'bits', factor: 1 },
  // symbols
  '%': { kind: 'percent', factor: 1 }, Hz: { kind: 'hz', factor: 1 }, Cel: { kind: 'cel', factor: 1 },
  // dimensionless ratio
  '1': { kind: 'none', factor: 1 },
};

/** Case-insensitive aliases, matched on the lower-cased spelling. Only spellings with a single
 *  possible meaning are here: no prefixed units (`mBy`/`MBy`) and no single letters (`S` is siemens,
 *  `H` henry in UCUM), so an ambiguous input falls through to "unrecognized" instead of being guessed. */
const UNIT_ALIASES: Record<string, UnitDef> = {
  by: UNIT_TABLE['By'], byte: UNIT_TABLE['By'], bytes: UNIT_TABLE['By'],
  ns: UNIT_TABLE['ns'], nanosecond: UNIT_TABLE['ns'], nanoseconds: UNIT_TABLE['ns'],
  us: UNIT_TABLE['us'], microsecond: UNIT_TABLE['us'], microseconds: UNIT_TABLE['us'],
  ms: UNIT_TABLE['ms'], millisecond: UNIT_TABLE['ms'], milliseconds: UNIT_TABLE['ms'],
  second: UNIT_TABLE['s'], seconds: UNIT_TABLE['s'], sec: UNIT_TABLE['s'], secs: UNIT_TABLE['s'],
  min: UNIT_TABLE['min'], minute: UNIT_TABLE['min'], minutes: UNIT_TABLE['min'],
  hour: UNIT_TABLE['h'], hours: UNIT_TABLE['h'], day: UNIT_TABLE['d'], days: UNIT_TABLE['d'],
  bits: UNIT_TABLE['bit'], hz: UNIT_TABLE['Hz'], percent: UNIT_TABLE['%'],
};

export interface ParsedUnit {
  kind: UnitKind;
  /** How many of the family's base unit (ms / bytes / bits) one of this unit is. */
  factor: number;
  /** The unit is itself a per-second rate (`By/s`, `{request}/s`). */
  perSecond: boolean;
  /** The spelling to show for an unrecognised unit, annotations removed. Empty for every other kind. */
  text: string;
}

/** Removes UCUM annotations (`{...}`) from a unit string: `{requests}` -> ``, `By{sent}` -> `By`,
 *  `{request}/s` -> `/s`. */
function stripAnnotations(raw: string): string {
  return raw.replace(/\{[^{}]*\}/g, '').trim();
}

/**
 * Reads an OTLP/UCUM unit string. Annotations are stripped first, so a unit that is only an
 * annotation (`{requests}`) is `none`, shown nowhere; a trailing `/s` marks a per-second rate; the
 * remainder is looked up case-sensitively, then through the unambiguous alias table; anything else
 * is `unknown` and keeps its (annotation-free) spelling.
 */
export function parseUnit(raw?: string | null): ParsedUnit {
  let rest = stripAnnotations(raw ?? '');
  let perSecond = false;
  if (rest.endsWith('/s')) { perSecond = true; rest = rest.slice(0, -2).trim(); }
  if (rest === '') return { kind: 'none', factor: 1, perSecond, text: '' };

  const def = UNIT_TABLE[rest] ?? UNIT_ALIASES[rest.toLowerCase()];
  if (def) return { kind: def.kind, factor: def.factor, perSecond, text: '' };
  return { kind: 'unknown', factor: 1, perSecond, text: perSecond ? `${rest}/s` : rest };
}

/** The unit as text for a chip, a table cell or a metadata row; empty when there is nothing to show
 *  (no unit, or a braced annotation like `{requests}`), so the caller can hide the element. */
export function formatUnitLabel(raw?: string | null): string {
  const p = parseUnit(raw);
  if (p.kind === 'none') return p.perSecond ? '/s' : '';
  if (p.kind === 'unknown') return p.text;
  return stripAnnotations(raw ?? '');
}

export interface FormatUnitOptions {
  /** The value is a per-second rate of a quantity measured in `unit` (a Sum shown as a rate). */
  perSecond?: boolean;
}

/**
 * Formats a raw metric value using its OTLP/UCUM `unit` string: time units auto-scale through
 * µs/ms/s (and min/h/d when the unit itself is minutes, hours or days), byte units auto-scale
 * through B/KiB/MiB/GiB/TiB (binary inputs) or B/kB/MB/GB/TB (decimal inputs), bits through
 * bit/kbit/Mbit/..., `%`, `Hz` and `Cel` carry their symbol, a braced annotation or a dimensionless
 * `1` is a bare number, and any other unit falls back to a plain number with its spelling appended.
 * A per-second rate (`options.perSecond`, or a unit that already ends in `/s`) gets a `/s` suffix.
 * Used for stat cards, axis/tooltip labels, exemplars and histogram bucket bounds so a chart's
 * numbers read in a unit-appropriate scale instead of the stored magnitude verbatim.
 */
export function formatUnitValue(value: number, unit?: string | null, options?: FormatUnitOptions): string {
  const p = parseUnit(unit);
  const rate = p.perSecond || options?.perSecond === true ? '/s' : '';
  const sign = value < 0 ? '-' : '';
  const abs = Math.abs(value);

  switch (p.kind) {
    case 'time': return sign + formatDurationMs(abs * p.factor) + rate;
    case 'longTime': return sign + formatLongDurationMs(abs * p.factor) + rate;
    case 'bytesBinary': return sign + formatBinaryBytes(abs * p.factor) + rate;
    case 'bytesDecimal': return sign + formatDecimalBytes(abs * p.factor) + rate;
    case 'bits': return sign + formatBits(abs * p.factor) + rate;
    case 'percent': return `${plainNumber(value)}%${rate}`;
    case 'hz': return `${plainNumber(value)} Hz${rate}`;
    case 'cel': return `${plainNumber(value)} °C${rate}`;
    case 'unknown': return `${plainNumber(value)} ${p.text}${options?.perSecond && !p.perSecond ? '/s' : ''}`;
    default: return plainNumber(value) + rate;
  }
}

/** Bare-number formatting shared by the unit-less fallback paths: whole thousands-separated numbers
 *  from 1,000 up (never a rounded-to-3-significant-digits value, which would turn 12,345 into 12,300),
 *  fixed precision for typical magnitudes, significant-digit precision for very small values. */
function plainNumber(v: number): string {
  const abs = Math.abs(v);
  if (abs === 0) return '0';
  if (abs >= 1e15) return Number(v.toPrecision(3)).toString();
  if (abs >= 1000) return Math.round(v).toLocaleString();
  if (abs < 0.01) return Number(v.toPrecision(3)).toString();
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

// ===========================================================================
// PHASE 6 (list-pages-server-side plan): chart-style views and cross-series fold, rebuilt
// against the server-bucketed `MetricBucketPoint` shape (MetricSeriesModels.cs). These are
// fresh implementations, not the pre-Phase-4 `MetricDataPoint`-based functions of the same
// visual purpose (deleted in that phase) — there is no client-side windowing/resampling left
// to do, since the server already delivers one bucket per (display series, time slot).
// ===========================================================================

/**
 * Shared categorical palette for multi-slice charts (donut share). Slices are assigned by index
 * modulo the length, matching the per-service coloring used elsewhere.
 */
export const CATEGORICAL_COLORS = [
  '#1976d2', '#f57c00', '#388e3c', '#7b1fa2', '#00838f', '#5d4037', '#558b2f', '#4527a0',
];

/**
 * Donut chart of share-of-total across slices (e.g. one slice per display series/service). Slices
 * arrive pre-reduced to a single value each; zero/negative values are dropped so the ring only
 * shows real contributions. Returns null if nothing positive remains.
 */
export function buildShareDonut(
  slices: { name: string; value: number }[],
  isDark: boolean,
  unit?: string | null,
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
    tooltip: { y: { formatter: (v: number) => formatUnitValue(v, unit) } },
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

function fmtBound(v: number, unit?: string | null): string {
  if (!isFinite(v)) return '∞';
  if (v === 0) return '0';
  return formatUnitValue(v, unit);
}

/** Human-readable range label per bucket, e.g. "< 5", "5 – 10", "≥ 100". */
function bucketLabels(counts: unknown[], bounds: number[], unit?: string | null): string[] {
  const labels: string[] = [];
  for (let i = 0; i < counts.length; i++) {
    if (i === 0) labels.push(`< ${fmtBound(bounds[0] ?? Infinity, unit)}`);
    else if (i < bounds.length) labels.push(`${fmtBound(bounds[i - 1], unit)} – ${fmtBound(bounds[i], unit)}`);
    else labels.push(`≥ ${fmtBound(bounds[bounds.length - 1], unit)}`);
  }
  return labels;
}

/** Shared ApexCharts heatmap config for the bucket-distribution charts. */
function renderHeatmap(
  series: { name: string; data: { x: number; y: number }[] }[],
  maxCount: number,
  isDark: boolean,
): ApexOptions {
  const height = Math.min(420, Math.max(180, series.length * 18));
  const zeroColor = isDark ? '#26272b' : '#f4f4f5';
  const separatorColor = gridLineColor(isDark);
  return {
    chart: { type: 'heatmap', height, toolbar: { show: false }, background: 'transparent', zoom: { allowMouseWheelZoom: false } },
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

/** Upper bound on heatmap Y-rows / bar X-categories; finer bucket schemas (exponential
 *  histograms) are merged down to this. Matches the pre-Phase-4 `MAX_HEATMAP_ROWS`. */
const MAX_HEATMAP_ROWS = 24;

/**
 * Groups an over-fine bucket schema into at most `maxRows` display bins by merging adjacent
 * buckets. Returns the group ranges `[start, end)` into the original counts, plus the merged
 * bounds (upper bound of each group except the final overflow group). Schemas already within
 * `maxRows` yield one group per bucket (identity).
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
    if (end < refLen) merged.push(bounds[end - 1]);
  }
  return { groups, bounds: merged };
}

/** The most common `bucketCounts` length among a set of already-bucketed points — the "reference"
 *  bucket layout to merge onto. Points with any other length are left out, mirroring decision 42's
 *  mismatched-layout handling (the server does this only within one display series; these two
 *  bucket-distribution helpers apply the same rule again across whatever points/series they were
 *  handed, since they may see points from more than one display series' worth of data). */
function referenceBucketLayout(points: MetricBucketPoint[]): { refLen: number; bounds: number[] } | null {
  const withBuckets = points.filter((p) => p.bucketCounts && p.bucketCounts.length);
  if (!withBuckets.length) return null;
  const lenCounts = new Map<number, number>();
  for (const p of withBuckets) lenCounts.set(p.bucketCounts!.length, (lenCounts.get(p.bucketCounts!.length) ?? 0) + 1);
  const refLen = [...lenCounts.entries()].sort((a, b) => b[1] - a[1])[0][0];
  const bounds = withBuckets.find((p) => p.bucketCounts!.length === refLen && p.bucketBounds?.length)?.bucketBounds ?? [];
  return { refLen, bounds };
}

/**
 * Bucket-distribution heatmap (X = bucket point time, Y = bucket range, color = count) built
 * directly from a display series' `MetricBucketPoint[]` — no client-side windowing, since the
 * server already delivers one set of bucket counts per time bucket. Over-fine schemas
 * (exponential histograms carry ~100+ buckets) are merged down to {@link MAX_HEATMAP_ROWS} rows.
 * Returns null if there is no usable bucket data.
 */
export function buildHistogramHeatmapFromBuckets(
  points: MetricBucketPoint[],
  isDark: boolean,
  unit?: string | null,
): ApexOptions | null {
  const layout = referenceBucketLayout(points);
  if (!layout) return null;
  const { refLen, bounds } = layout;
  const { groups, bounds: rowBounds } = planBucketGroups(refLen, bounds, MAX_HEATMAP_ROWS);
  const labels = bucketLabels(new Array(groups.length), rowBounds, unit);

  const series = labels.map((label) => ({ name: label, data: [] as { x: number; y: number }[] }));
  let maxCount = 0;
  for (const p of points) {
    if (!p.bucketCounts || p.bucketCounts.length !== refLen) continue;
    const x = new Date(p.timestamp).getTime();
    for (let r = 0; r < groups.length; r++) {
      const [s, e] = groups[r];
      let y = 0;
      for (let i = s; i < e; i++) y += p.bucketCounts[i] ?? 0;
      series[r].data.push({ x, y });
      if (y > maxCount) maxCount = y;
    }
  }
  if (maxCount === 0) return null;
  return renderHeatmap(series, maxCount, isDark);
}

/**
 * Bar chart of the bucket distribution summed across every bucket point handed in — the
 * whole-range "overall shape" view. Over-fine schemas are merged down to at most
 * {@link MAX_HEATMAP_ROWS} bars. Returns null if there is no usable bucket data.
 */
export function buildHistogramBarFromBuckets(
  points: MetricBucketPoint[],
  isDark: boolean,
  unit?: string | null,
): ApexOptions | null {
  const layout = referenceBucketLayout(points);
  if (!layout) return null;
  const { refLen, bounds } = layout;

  const counts = new Array(refLen).fill(0);
  for (const p of points) {
    if (!p.bucketCounts || p.bucketCounts.length !== refLen) continue;
    for (let i = 0; i < refLen; i++) counts[i] += p.bucketCounts[i] ?? 0;
  }
  if (counts.reduce((a: number, b: number) => a + b, 0) === 0) return null;

  const { groups, bounds: barBounds } = planBucketGroups(refLen, bounds, MAX_HEATMAP_ROWS);
  const grouped = groups.map(([s, e]) => {
    let sum = 0;
    for (let i = s; i < e; i++) sum += counts[i];
    return sum;
  });
  const labels = bucketLabels(new Array(groups.length), barBounds, unit);

  return {
    chart: { type: 'bar', height: 220, toolbar: { show: false }, background: 'transparent', zoom: { allowMouseWheelZoom: false } },
    theme: { mode: isDark ? 'dark' : 'light' },
    series: [{ name: 'Count', data: grouped }],
    xaxis: { categories: labels, labels: { rotate: -45, hideOverlappingLabels: true } },
    dataLabels: { enabled: false },
    legend: { show: false },
    grid: chartGrid(isDark),
    plotOptions: { bar: { columnWidth: '80%' } },
  };
}

/**
 * Index-wise fold of every display series (plus "other") sharing one response's identical dense
 * bucket grid into a single aggregate `MetricBucketPoint[]` — the "Aggregate: All" cross-series
 * view (decision 22's per-type math applied one level higher, extended per phase 6 to Histogram/
 * Exponential-Histogram/Summary, not just Gauge/Sum). Safe because every group in one response
 * shares the same bucket count/width (`MergeBucketsForDisplaySeries` on the server always emits
 * one point per bucket index, even when empty).
 *
 * For Histogram/Exponential-Histogram, the server only guarantees one bucket layout *within* a
 * display series (decision 42); folding *across* display series (e.g. across services) can still
 * mix layouts, so each bucket index picks its own majority layout and drops points whose
 * `bucketCounts` length disagrees, the same rule {@link buildHistogramHeatmapFromBuckets}/
 * {@link buildHistogramBarFromBuckets} apply. This is a bucket-index-local approximation of
 * decision 42, not the server's exact algorithm (which ranks by observation count across the
 * whole window, not per bucket) — acceptable here since this fold is a client-only convenience
 * view, not the canonical per-series chart.
 */
/** Σ of the defined values, or undefined when none is — an unmeasured rate isn't 0/s. */
export function sumDefined(values: (number | undefined)[]): number | undefined {
  const present = values.filter((v): v is number => v != null);
  return present.length ? present.reduce((a, b) => a + b, 0) : undefined;
}

export function foldPointsAcrossGroups(
  groups: { points: MetricBucketPoint[] }[],
  type: MetricType,
): MetricBucketPoint[] {
  if (!groups.length) return [];
  const bucketCount = Math.max(...groups.map((g) => g.points.length), 0);
  const out: MetricBucketPoint[] = [];
  const isGauge = type === MetricType.Gauge;
  const isDist = type === MetricType.Histogram || type === MetricType.ExponentialHistogram;
  const isSummary = type === MetricType.Summary;

  for (let i = 0; i < bucketCount; i++) {
    const pts = groups.map((g) => g.points[i]).filter((p): p is MetricBucketPoint => p != null);
    const timestamp = pts[0]?.timestamp ?? '';
    if (!pts.length) { out.push({ timestamp }); continue; }

    if (isDist) {
      const withBuckets = pts.filter((p) => p.bucketCounts && p.bucketCounts.length);
      if (!withBuckets.length) { out.push({ timestamp, count: 0, sum: 0 }); continue; }
      const lenCounts = new Map<number, number>();
      for (const p of withBuckets) lenCounts.set(p.bucketCounts!.length, (lenCounts.get(p.bucketCounts!.length) ?? 0) + 1);
      const majorityLen = [...lenCounts.entries()].sort((a, b) => b[1] - a[1])[0][0];
      const matching = withBuckets.filter((p) => p.bucketCounts!.length === majorityLen);
      const bounds = matching.find((p) => p.bucketBounds?.length)?.bucketBounds;
      const counts = new Array(majorityLen).fill(0);
      let count = 0; let sum = 0; let min: number | undefined; let max: number | undefined; let minMaxApproximate = false;
      const rate = sumDefined(matching.map((p) => p.rate));
      for (const p of matching) {
        for (let k = 0; k < majorityLen; k++) counts[k] += p.bucketCounts![k] ?? 0;
        count += p.count ?? 0;
        sum += p.sum ?? 0;
        if (p.min != null) min = min == null ? p.min : Math.min(min, p.min);
        if (p.max != null) max = max == null ? p.max : Math.max(max, p.max);
        if (p.minMaxApproximate) minMaxApproximate = true;
      }
      out.push({ timestamp, count, sum, min, max, minMaxApproximate, rate, bucketCounts: counts, bucketBounds: bounds });
      continue;
    }

    if (isSummary) {
      const withQ = pts.filter((p) => p.quantiles && p.quantiles.length);
      if (!withQ.length) { out.push({ timestamp }); continue; }
      const fracs = new Set<number>();
      for (const p of withQ) for (const q of p.quantiles!) fracs.add(q);
      const sorted = [...fracs].sort((a, b) => a - b);
      const quantileValues: number[] = [];
      for (const q of sorted) {
        const vals = withQ
          .map((p) => {
            const idx = p.quantiles!.findIndex((x) => Math.abs(x - q) < 1e-7);
            return idx >= 0 ? p.quantileValues?.[idx] : undefined;
          })
          .filter((v): v is number => v != null);
        quantileValues.push(vals.length ? vals.reduce((a, b) => a + b, 0) / vals.length : NaN);
      }
      out.push({
        timestamp,
        quantiles: sorted,
        quantileValues,
        count: withQ.reduce((a, p) => a + (p.count ?? 0), 0),
        sum: withQ.reduce((a, p) => a + (p.sum ?? 0), 0),
        rate: sumDefined(withQ.map((p) => p.rate)),
        isApproximate: withQ.length > 1 || withQ.some((p) => p.isApproximate),
      });
      continue;
    }

    // Gauge/Sum
    const vals = pts.map((p) => p.value).filter((v): v is number => v != null);
    const value = vals.length
      ? (isGauge ? vals.reduce((a, b) => a + b, 0) / vals.length : vals.reduce((a, b) => a + b, 0))
      : undefined;
    const mins = pts.map((p) => p.min).filter((v): v is number => v != null);
    const maxs = pts.map((p) => p.max).filter((v): v is number => v != null);
    out.push({
      timestamp,
      value,
      rate: isGauge ? undefined : sumDefined(pts.map((p) => p.rate)),
      min: mins.length ? Math.min(...mins) : undefined,
      max: maxs.length ? Math.max(...maxs) : undefined,
    });
  }
  return out;
}
