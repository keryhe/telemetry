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

export interface MetricSeries {
  name: string;
  type: MetricType;
  labels: Record<string, string>;
  points: MetricDataPoint[];
  /** True when the server-side row cap was hit for at least one underlying metric row — the
   *  requested range may hold more data than shown. */
  truncated?: boolean;
}

export interface NamedMetricSeries {
  seriesName: string;
  metricId: number;
  serviceName: string;
  labels: Record<string, string>;
  points: MetricDataPoint[];
}

export interface MultiSeriesMetricData {
  name: string;
  type: MetricType;
  series: NamedMetricSeries[];
  /** True when the server-side row cap was hit for at least one underlying metric row — the
   *  requested range may hold more data than shown. */
  truncated?: boolean;
}

export interface MetricSeriesParams {
  metricName: string;
  start?: Date;
  end?: Date;
  metricId?: number;
  labelFilters?: Record<string, string>;
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
  /** True when the scan hit its cap: more exemplars exist beyond those returned. */
  hasMore: boolean;
}
