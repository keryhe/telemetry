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
