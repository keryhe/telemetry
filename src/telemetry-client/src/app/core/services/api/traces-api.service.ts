import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { APP_CONFIG } from '../../config/app-config';
import { OperationStats, ServiceDependency, ServiceStats, SpanModel, TraceInfo } from '../../models/trace.models';
import { TimeBucket } from '../../../shared/utils/chart.utils';

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
  serviceCount: number;
  lastTraceStartTime: Date | null;
}

interface TraceSummaryDto {
  source: 'rollup' | 'raw';
  buckets: (TimeBucket & { timestamp: string })[];
  summary: Omit<TraceWindowSummary, 'lastTraceStartTime'> & { lastTraceStartTime: string | null };
  services: ServiceStats[];
  latencyBuckets: (Omit<TraceLatencyBucket, 'xStart' | 'xEnd'> & { xStart: string; xEnd: string })[];
  listTotal: number;
  requestCount: number;
  totalIsLowerBound: boolean;
  newSinceAsOf: number;
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
  newSinceAsOf: number;
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
  private readonly base = `${inject(APP_CONFIG).apiUrl}/traces`;

  /** Chart/stat-card summary — volume/error/duration buckets, per-service RED stats, the latency heatmap, and listTotal/requestCount (decision 13). */
  getTraceSummary(query: TraceSummaryQuery): Observable<TraceSummaryResult> {
    let params = this.filterParams(query)
      .set('bucketCount', query.bucketCount ?? 60)
      .set('latencyDurationRows', query.latencyDurationRows ?? 20);
    return this.http.get<TraceSummaryDto>(`${this.base}/summary`, { params }).pipe(
      map((dto) => ({
        source: dto.source,
        buckets: dto.buckets.map((b) => ({ ...b, timestamp: new Date(b.timestamp) })),
        summary: { ...dto.summary, lastTraceStartTime: dto.summary.lastTraceStartTime ? new Date(dto.summary.lastTraceStartTime) : null },
        services: dto.services,
        latencyBuckets: dto.latencyBuckets.map((b) => ({ ...b, xStart: new Date(b.xStart), xEnd: new Date(b.xEnd) })),
        listTotal: dto.listTotal,
        requestCount: dto.requestCount,
        totalIsLowerBound: dto.totalIsLowerBound,
        newSinceAsOf: dto.newSinceAsOf,
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

  /** Dashboard's Recent Errors / Slowest Traces widgets. */
  getTraceSamples(start: Date, end: Date, kind: 'errors' | 'slowest', limit = 5): Observable<TraceInfo[]> {
    const params = new HttpParams()
      .set('start', start.toISOString())
      .set('end', end.toISOString())
      .set('kind', kind)
      .set('limit', limit);
    return this.http.get<TraceInfo[]>(`${this.base}/samples`, { params });
  }

  getSpans(traceId: string): Observable<SpanModel[]> {
    return this.http.get<SpanModel[]>(`${this.base}/${traceId}/spans`);
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
   * `X-Tenant-Id` interceptor still runs — see `downloadBlob`'s doc comment.
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
