export enum MetricType {
  Gauge = 0,
  Sum = 1,
  Histogram = 2,
  ExponentialHistogram = 3,
  Summary = 4,
}

export enum AggregationTemporality {
  Unspecified = 0,
  Delta = 1,
  Cumulative = 2,
}

export const TYPE_LABELS: Record<MetricType, string> = {
  [MetricType.Gauge]: 'Gauge',
  [MetricType.Sum]: 'Counter',
  [MetricType.Histogram]: 'Histogram',
  [MetricType.ExponentialHistogram]: 'Exp. Histogram',
  [MetricType.Summary]: 'Summary',
};

// Chosen to stay legible as a chip outline in both light and dark mode (contrast-checked against
// each theme's --mat-sys-background) and to stay clear of colors already meaningful elsewhere in
// the app — the red/orange/green severity palette (log.models.ts SEVERITY_COLORS) and the primary
// blue used for info/ok states.
export const TYPE_COLORS: Record<MetricType, string> = {
  [MetricType.Gauge]: '#5C6BC0',
  [MetricType.Sum]: '#0097A7',
  [MetricType.Histogram]: '#D81B60',
  [MetricType.ExponentialHistogram]: '#7E57C2',
  [MetricType.Summary]: '#827717',
};

export function getTypeLabel(type: MetricType): string {
  return TYPE_LABELS[type] ?? 'Unknown';
}

export function getTypeColor(type: MetricType): string {
  return TYPE_COLORS[type] ?? '#9e9e9e';
}

export interface MetricInfo {
  id: number;
  name: string;
  description?: string;
  unit?: string;
  type: MetricType;
  serviceName?: string;
  firstSeen: string;
  lastSeen: string;
  dataPointCount: number;
}

export interface MetricTypeCount {
  type: MetricType;
  count: number;
}

/** True (unbounded) distinct-metric-name counts per type, unaffected by the /api/metrics row limit. */
export interface MetricsSummary {
  uniqueMetricCount: number;
  countsByType: MetricTypeCount[];
}

export interface MetricDataPoint {
  startTimestamp?: string;
  timestamp: string;
  doubleValue?: number;
  intValue?: number;
  count?: number;
  sum?: number;
  min?: number;
  max?: number;
  flags: number;
  bucketCounts?: number[];
  bucketBounds?: number[];
  // Exponential histogram fields
  scale?: number;
  zeroCount?: number;
  positiveOffset?: number;
  positiveBucketCounts?: number[];
  negativeOffset?: number;
  negativeBucketCounts?: number[];
  // Summary fields
  quantiles?: number[];
  quantileValues?: number[];
  attributes?: Record<string, unknown>;
  aggregationTemporality?: AggregationTemporality;
  isMonotonic?: boolean;
}

export interface ExemplarModel {
  timeUnixNano: number;
  valueDouble?: number;
  valueInt?: number;
  spanIdHex?: string;
  traceIdHex?: string;
  filteredAttributes?: Record<string, unknown>;
}

// =============================================================================
// Phase 4 (list-pages-server-side plan): database-side, pre-bucketed metric series.
// Mirrors Keryhe.Telemetry.Core.Models.MetricSeriesModels.cs exactly — see that file for the
// aggregation semantics (per-type bucket math, top-N + "other" folding, mismatched histogram
// bucket layouts). The client no longer windows, groups or computes percentiles itself.
// =============================================================================

/** One pre-aggregated bucket for a display series. Which fields are populated depends on the
 *  metric type: `value`/`min`/`max` for gauge/sum; `count`/`sum`/`bucketCounts`/`bucketBounds`/
 *  `min`/`max` for histogram and exponential histogram; `quantiles`/`quantileValues` for summary. */
export interface MetricBucketPoint {
  timestamp: string;
  /** Gauge: bucket average. Sum: bucket delta (summed across streams), already normalized —
   *  the same shape whether the underlying sum was delta or cumulative temporality. */
  value?: number;
  min?: number;
  max?: number;
  count?: number;
  sum?: number;
  bucketCounts?: number[];
  bucketBounds?: number[];
  quantiles?: number[];
  quantileValues?: number[];
  /** Summary only: true when more than one stream contributed — the quantiles shown are an
   *  average-of-quantiles approximation, not a true merged quantile. */
  isApproximate?: boolean;
}

export interface DisplayMetricSeries {
  seriesName: string;
  serviceName: string;
  labels: Record<string, string>;
  points: MetricBucketPoint[];
  /** Histogram/exponential-histogram only: count of streams folded into this display series whose
   *  bucket layout didn't match the layout actually charted. Only render the "N streams not shown"
   *  note when this is a positive number. */
  excludedStreams?: number | null;
}

export interface OtherMetricSeries {
  /** Number of display series folded into "other". */
  seriesCount: number;
  points: MetricBucketPoint[];
  excludedStreams?: number | null;
}

export interface MetricSeriesResult {
  name: string;
  type: MetricType;
  unit?: string;
  bucketWidthMs: number;
  /** True when the full-resolution query timed out and a quarter-resolution retry was used, or
   *  when that retry also timed out — `series`/`other` are empty in the latter case. */
  timedOut: boolean;
  series: DisplayMetricSeries[];
  other?: OtherMetricSeries | null;
}

export interface MetricSeriesQueryParams {
  metricName: string;
  start: Date;
  end: Date;
  metricId?: number;
  labelFilters?: Record<string, string>;
  q?: string;
  /** Target point count per stream, derived from chart pixel width — never persisted in URL state. */
  points: number;
  top?: number;
}

export interface MetricExemplarQueryParams {
  metricName: string;
  start: Date;
  end: Date;
  metricId?: number;
  labelFilters?: Record<string, string>;
  q?: string;
  size?: number;
  /** Opaque, unparsed keyset cursor (analytics tier only). */
  cursor?: string | null;
  nav?: 'first' | 'next' | 'prev' | 'last';
}

/** One exemplar plus the identity of the data point and series it was sampled from. Served by the
 *  dedicated /metrics/exemplars endpoint, called only when the Exemplars tab is opened. */
export interface MetricExemplar {
  exemplar: ExemplarModel;
  seriesName: string;
  serviceName: string;
  labels: Record<string, string>;
  pointTimestamp: string;
  /** Owning point's observation count — distributions only; absent for gauge/sum. */
  pointCount?: number;
  /** Owning point's value — gauge/sum only; absent for distributions. */
  pointDoubleValue?: number;
  pointIntValue?: number;
}

export interface MetricExemplarPage {
  name: string;
  type: MetricType;
  exemplars: MetricExemplar[];

  // Analytics tier: real keyset paging.
  nextCursor?: string | null;
  prevCursor?: string | null;
  total?: number | null;
  totalIsLowerBound: boolean;

  // Standard tier: newest-500, no cursor.
  /** True when the standard-tier scan hit its 500-row cap: more exemplars exist beyond those returned. */
  capped: boolean;
}

// =============================================================================
// Phase 5 (list-pages-server-side plan): server-paged metrics catalog. Mirrors
// Keryhe.Telemetry.Core.Models.Metrics.cs's MetricCatalogQuery/MetricCatalogPage exactly.
// Replaces the former GET /api/metrics (getAllMetrics) call and its client-side
// uniqueMetrics/filteredUnique/filteredAll grouping.
// =============================================================================

/** One row of the groupBy=name view (reuses the server's UniqueMetricSummary shape). */
export interface UniqueMetricSummary {
  name: string;
  type: MetricType;
  unit?: string;
  description?: string;
  instanceCount: number;
  services: string[];
  lastSeen: string;
}

export type MetricCatalogGroupBy = 'instance' | 'name';

export interface MetricCatalogQueryParams {
  start: Date;
  end: Date;
  q?: string;
  service?: string;
  type?: MetricType;
  groupBy: MetricCatalogGroupBy;
  size?: number;
  /** Opaque, unparsed keyset cursor. */
  cursor?: string | null;
  nav?: 'first' | 'next' | 'prev' | 'last';
}

/** Exactly one of `items` (groupBy=instance) or `names` (groupBy=name) is populated, matching the request's groupBy. */
export interface MetricCatalogPage {
  items: MetricInfo[];
  names: UniqueMetricSummary[];
  nextCursor?: string | null;
  prevCursor?: string | null;
  total?: number | null;
  totalIsLowerBound: boolean;
}
