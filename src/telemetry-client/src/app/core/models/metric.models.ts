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
  description: string | null;
  unit: string | null;
  type: MetricType;
  serviceName: string | null;
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
  startTimestamp: string | null;
  timestamp: string;
  doubleValue: number | null;
  intValue: number | null;
  count: number | null;
  sum: number | null;
  min: number | null;
  max: number | null;
  flags: number;
  bucketCounts: number[] | null;
  bucketBounds: number[] | null;
  // Exponential histogram fields
  scale: number | null;
  zeroCount: number | null;
  positiveOffset: number | null;
  positiveBucketCounts: number[] | null;
  negativeOffset: number | null;
  negativeBucketCounts: number[] | null;
  // Summary fields
  quantiles: number[] | null;
  quantileValues: number[] | null;
  attributes: Record<string, unknown> | null;
  exemplars: ExemplarModel[] | null;
  aggregationTemporality: AggregationTemporality | null;
  isMonotonic: boolean | null;
}

export interface ExemplarModel {
  timeUnixNano: number;
  valueDouble: number | null;
  valueInt: number | null;
  spanIdHex: string | null;
  traceIdHex: string | null;
  filteredAttributes: Record<string, unknown> | null;
}

export interface MetricSeries {
  name: string;
  type: MetricType;
  labels: Record<string, string>;
  points: MetricDataPoint[];
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
}

export interface MetricSeriesParams {
  metricName: string;
  start?: Date;
  end?: Date;
  metricId?: number;
  labelFilters?: Record<string, string>;
}
