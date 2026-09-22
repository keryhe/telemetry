export enum SpanKind {
  Unspecified = 0,
  Internal = 1,
  Server = 2,
  Client = 3,
  Producer = 4,
  Consumer = 5,
}

export enum SpanStatusCode {
  Unset = 0,
  Ok = 1,
  Error = 2,
}

export interface TraceInfo {
  traceIdHex: string;
  spanCount: number;
  traceStartTime: string;
  traceEndTime: string;
  /** Whole-trace duration, or the filtered service's own duration when a service filter matched this trace. */
  traceDuration: string;
  serviceName?: string;
  rootOperationName?: string;
  hasErrors: boolean;
  services: string[];
  rootSpanAttributes?: Record<string, unknown>;
  /** The trace's true root span, or the filtered service's entry span when a service filter matched this trace. Used to deep-link into trace-detail. */
  displaySpanIdHex?: string;
}

export interface SpanModel {
  traceIdHex: string;
  spanIdHex: string;
  parentSpanIdHex?: string;
  name: string;
  kind: SpanKind;
  startTimeUnixNano: number;
  endTimeUnixNano: number;
  statusCode: SpanStatusCode;
  statusMessage?: string;
  traceState?: string;
  flags: number;
  droppedAttributesCount: number;
  droppedEventsCount: number;
  droppedLinksCount: number;
  attributes?: Record<string, unknown>;
  events: SpanEventModel[];
  links: SpanLinkModel[];
  resource?: ResourceModel;
  instrumentationScope?: InstrumentationScopeModel;
}

export interface SpanEventModel {
  name: string;
  timeUnixNano: number;
  attributes?: Record<string, unknown>;
}

export interface SpanLinkModel {
  linkedTraceIdHex: string;
  linkedSpanIdHex: string;
  flags: number;
  attributes?: Record<string, unknown>;
}

export interface ResourceModel {
  schemaUrl?: string;
  attributes: Record<string, unknown>;
}

export interface InstrumentationScopeModel {
  name: string;
  version?: string;
  attributes: Record<string, unknown>;
}

export interface OperationStats {
  operation: string;
  count: number;
  errorCount: number;
  errorRate: number;      // 0–100
  ratePerSecond: number;
  avgMs: number;
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
}

export interface ServiceDependency {
  parentService: string;
  childService: string;
  callCount: number;
  avgDurationMs: number;
  errorCount: number;
  errorRate: number;
}

/** Per-service RED metrics for the dashboard's service health table. */
export interface ServiceStats {
  service: string;
  count: number;
  errorCount: number;
  errorRate: number;      // 0–100
  ratePerSecond: number;
  avgMs: number;
  p95Ms: number;
}

export interface TraceFilter {
  start: Date;
  end: Date;
  limit?: number;
  mode?: 'all' | 'errors' | 'slow';
  service?: string;
  minDurationMs?: number;
}
