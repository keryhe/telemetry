import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { APP_CONFIG } from '../../config/app-config';
import { TenantService } from '../tenant.service';
import { tenantApiUrl } from './tenant-api-url';
import {
  InstrumentationScopeModel, OperationStats, ResourceModel, ServiceDependency, ServiceStats, SpanModel, TraceInfo,
} from '../../models/trace.models';
import { TimeBucket } from '../../../shared/utils/chart.utils';

/** Set by `samples` when its anchor scan ran out of time (`TracesController.TimedOutHeader`). */
const TIMED_OUT_HEADER = 'X-Telemetry-Timed-Out';

export interface TraceSamplesResult {
  items: TraceInfo[];
  /** The scan ran out of time: `items` is empty because the answer is unknown, not because nothing matched. */
  timedOut: boolean;
}

/** Filter shape shared by summary/page (list-pages-server-side plan, Phase 3 Target API). */
export interface TraceListFilter {
  start: Date;
  end: Date;
  // Opaque and never parsed into a Date (same contract as LogFilter.asOf — see that type's own
  // doc comment: the server's asOf carries sub-millisecond precision a JS Date can't hold).
  asOf?: string;
  mode?: 'all' | 'errors' | 'slow';
  service?: string;
  operation?: string;
  minDurationMs?: number;
  maxDurationMs?: number;
  q?: string;
}

export interface TraceSummaryQuery extends TraceListFilter {
  bucketCount?: number;
  latencyDurationRows?: number;
}

export interface TracePageQuery extends TraceListFilter {
  size: number;
  cursor?: string;
  nav?: 'first' | 'next' | 'prev' | 'last';
}

/** One cell of the trace latency chart's time × log-duration grid. */
export interface TraceLatencyBucket {
  xStart: Date;
  xEnd: Date;
  yStartMs: number;
  yEndMs: number;
  count: number;
  errorCount: number;
  /** Set only when count === 1 — the only case the bubble-click handler needs a trace id for. */
  sampleTraceIdHex?: string;
}

export interface TraceWindowSummary {
  count: number;
  errorCount: number;
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
}

/** `GET /api/tenants/{tenantId}/traces/{id}/spans`: spans plus the distinct resources and scopes they refer to by index. */
interface TraceDetailDto {
  resources: ResourceModel[];
  scopes: InstrumentationScopeModel[];
  spans: (Omit<SpanModel, 'resource' | 'instrumentationScope'> & { resourceIndex: number; scopeIndex: number })[];
}

interface TraceSummaryDto {
  source: 'rollup' | 'raw';
  buckets: (TimeBucket & { timestamp: string })[];
  summary: TraceWindowSummary;
  services: ServiceStats[];
  latencyBuckets: (Omit<TraceLatencyBucket, 'xStart' | 'xEnd'> & { xStart: string; xEnd: string })[];
  listTotal: number;
  requestCount: number;
  totalIsLowerBound: boolean;
  timedOut: boolean;
  asOf: string;
}

export interface TraceSummaryResult {
  source: 'rollup' | 'raw';
  buckets: TimeBucket[];
  summary: TraceWindowSummary;
  services: ServiceStats[];
  latencyBuckets: TraceLatencyBucket[];
  listTotal: number;
  requestCount: number;
  totalIsLowerBound: boolean;
  /** The server's anchor scan ran out of time: buckets/summary/services/latencyBuckets are empty (not "no traces") and listTotal is a capped count. */
  timedOut: boolean;
  asOf: string;
}

export interface TracePageResult {
  items: TraceInfo[];
  nextCursor: string | null;
  prevCursor: string | null;
  asOf: string;
}

@Injectable({ providedIn: 'root' })
export class TracesApiService {
  private readonly http = inject(HttpClient);
  private readonly tenant = inject(TenantService);
  private readonly apiUrl = inject(APP_CONFIG).apiUrl;
  /** Resolved per call: the tenant is the route's, and changes with it. */
  private get base(): string { return `${tenantApiUrl(this.apiUrl, this.tenant.requireTenantId())}/traces`; }

  /** Chart/stat-card summary — volume/error/duration buckets, per-service RED stats, the latency heatmap, and listTotal/requestCount (decision 13). */
  getTraceSummary(query: TraceSummaryQuery): Observable<TraceSummaryResult> {
    let params = this.filterParams(query)
      .set('bucketCount', query.bucketCount ?? 60)
      .set('latencyDurationRows', query.latencyDurationRows ?? 20);
    return this.http.get<TraceSummaryDto>(`${this.base}/summary`, { params }).pipe(
      map((dto) => ({
        source: dto.source,
        buckets: dto.buckets.map((b) => ({ ...b, timestamp: new Date(b.timestamp) })),
        summary: dto.summary,
        services: dto.services,
        latencyBuckets: dto.latencyBuckets.map((b) => ({ ...b, xStart: new Date(b.xStart), xEnd: new Date(b.xEnd) })),
        listTotal: dto.listTotal,
        requestCount: dto.requestCount,
        totalIsLowerBound: dto.totalIsLowerBound,
        timedOut: dto.timedOut ?? false,
        asOf: dto.asOf,
      }))
    );
  }

  /** Keyset-paged trace rows (decision 1), anchored on roots plus orphan traces (decision 41), pinned on `asOf` (decision 3). */
  getTracePage(query: TracePageQuery): Observable<TracePageResult> {
    let params = this.filterParams(query).set('size', query.size).set('nav', query.nav ?? 'first');
    if (query.cursor) params = params.set('cursor', query.cursor);
    return this.http.get<TracePageResult>(`${this.base}/page`, { params });
  }

  /**
   * Dashboard's Recent Errors / Slowest Traces widgets. The body is a bare array; a timed-out scan is flagged by the
   * `X-Telemetry-Timed-Out` header instead, and its empty array means "unknown", not "none".
   */
  getTraceSamples(start: Date, end: Date, kind: 'errors' | 'slowest', limit = 5): Observable<TraceSamplesResult> {
    const params = new HttpParams()
      .set('start', start.toISOString())
      .set('end', end.toISOString())
      .set('kind', kind)
      .set('limit', limit);
    return this.http.get<TraceInfo[]>(`${this.base}/samples`, { params, observe: 'response' }).pipe(
      map((response) => ({
        items: response.body ?? [],
        timedOut: response.headers.get(TIMED_OUT_HEADER) === 'true',
      })),
    );
  }

  /**
   * The trace's spans. `start` and `end` are the trace's extent as the list returned it (`traceStartTime`/`traceEndTime`): they
   * only let a provider that cannot seek a trace id (Timescale, ClickHouse) read that range, so both are optional and a deep link
   * without them still works (the read is then unbounded and the trace whole).
   */
  getSpans(traceId: string, start?: string, end?: string): Observable<SpanModel[]> {
    const params = start && end ? new HttpParams().set('start', start).set('end', end) : undefined;
    return this.http.get<TraceDetailDto>(`${this.base}/${traceId}/spans`, { params }).pipe(
      map((dto) => {
        // Each distinct resource and scope arrives once; hand every span a reference to its own, as the components expect.
        return dto.spans.map(({ resourceIndex, scopeIndex, ...span }) => ({
          ...span,
          resource: dto.resources[resourceIndex],
          instrumentationScope: dto.scopes[scopeIndex],
        }));
      })
    );
  }

  getDependencies(start?: Date, end?: Date): Observable<ServiceDependency[]> {
    let params = new HttpParams();
    if (start) params = params.set('start', start.toISOString());
    if (end) params = params.set('end', end.toISOString());
    return this.http.get<ServiceDependency[]>(`${this.base}/dependencies`, { params });
  }

  getOperationCounts(service: string, start?: Date, end?: Date): Observable<Record<string, number>> {
    let params = new HttpParams().set('service', service);
    if (start) params = params.set('start', start.toISOString());
    if (end) params = params.set('end', end.toISOString());
    return this.http.get<Record<string, number>>(`${this.base}/operations`, { params });
  }

  getLatencies(service: string, start?: Date, end?: Date): Observable<Record<string, number>> {
    let params = new HttpParams().set('service', service);
    if (start) params = params.set('start', start.toISOString());
    if (end) params = params.set('end', end.toISOString());
    return this.http.get<Record<string, number>>(`${this.base}/latencies`, { params });
  }

  /** Per-operation RED metrics (rate, error%, p50/p95/p99, avg) for a service over the window. */
  getOperationStats(service: string, start: Date, end: Date): Observable<OperationStats[]> {
    const params = new HttpParams()
      .set('service', service)
      .set('start', start.toISOString())
      .set('end', end.toISOString());
    return this.http.get<OperationStats[]>(`${this.base}/operations/stats`, { params });
  }

  /**
   * Streaming export (list-pages-server-side plan, Phase 8): one trace-summary row per trace, same
   * filters as {@link getTraceSummary}/{@link getTracePage}. Fetched as a Blob so the
   * auth interceptor still runs — see `downloadBlob`'s doc comment.
   */
  getTraceExport(query: TraceListFilter, format: 'ndjson' | 'csv'): Observable<Blob> {
    const params = this.filterParams(query).set('format', format);
    return this.http.get(`${this.base}/export`, { params, responseType: 'blob' });
  }

  private filterParams(query: TraceListFilter): HttpParams {
    let params = new HttpParams()
      .set('start', query.start.toISOString())
      .set('end', query.end.toISOString())
      .set('mode', query.mode ?? 'all');
    if (query.asOf) params = params.set('asOf', query.asOf);
    if (query.service) params = params.set('service', query.service);
    if (query.operation) params = params.set('operation', query.operation);
    if (query.minDurationMs != null) params = params.set('minDurationMs', query.minDurationMs);
    if (query.maxDurationMs != null) params = params.set('maxDurationMs', query.maxDurationMs);
    if (query.q) params = params.set('q', query.q);
    return params;
  }
}
