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
  /** The anchor span's own duration (end - start). The anchor is the trace's earliest span — or, when a service filter matched, that service's own earliest span. */
  traceDuration: string;
  /** The anchor span's service. */
  serviceName?: string;
  /** The anchor span's name — what the operation filter matches. */
  rootOperationName?: string;
  /** The anchor span's kind (SERVER, CLIENT, ...). */
  anchorKind?: string;
  /** Whether any span in scope (the selected service's spans when one is selected, else the whole trace) has an ERROR status. */
  hasErrors: boolean;
  services: string[];
  rootSpanAttributes?: Record<string, unknown>;
  /** The anchor span (the trace's earliest span, or the filtered service's own earliest span). Used to deep-link into trace-detail. */
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
  p50Ms: number;
  p90Ms: number;
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
