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
  traceDuration: string;
  serviceName: string | null;
  rootOperationName: string | null;
  hasErrors: boolean;
  services: string[];
  rootSpanAttributes: Record<string, unknown> | null;
}

export interface SpanModel {
  traceIdHex: string;
  spanIdHex: string;
  parentSpanIdHex: string | null;
  name: string;
  kind: SpanKind;
  startTimeUnixNano: number;
  endTimeUnixNano: number;
  statusCode: SpanStatusCode;
  statusMessage: string | null;
  traceState: string | null;
  droppedAttributesCount: number;
  droppedEventsCount: number;
  droppedLinksCount: number;
  attributes: Record<string, unknown> | null;
  events: SpanEventModel[];
  links: SpanLinkModel[];
  resource: ResourceModel | null;
  instrumentationScope: InstrumentationScopeModel | null;
}

export interface SpanEventModel {
  name: string;
  timeUnixNano: number;
  attributes: Record<string, unknown> | null;
}

export interface SpanLinkModel {
  linkedTraceIdHex: string;
  linkedSpanIdHex: string;
  attributes: Record<string, unknown> | null;
}

export interface ResourceModel {
  schemaUrl: string | null;
  attributes: Record<string, unknown>;
}

export interface InstrumentationScopeModel {
  name: string;
  version: string | null;
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

export interface TraceFilter {
  start: Date;
  end: Date;
  limit?: number;
  mode?: 'all' | 'errors' | 'slow';
  service?: string;
  minDurationMs?: number;
}
