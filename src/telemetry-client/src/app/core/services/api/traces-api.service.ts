import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { APP_CONFIG } from '../../config/app-config';
import { OperationStats, ServiceDependency, ServiceStats, SpanModel, TraceFilter, TraceInfo } from '../../models/trace.models';
import { PagedResult } from '../../models/paged.models';
import { TimeBucket } from '../../../shared/utils/chart.utils';

export interface TraceSearchQuery extends TraceFilter {
  offset: number;
  sort?: string;
  dir?: 'asc' | 'desc';
  operation?: string;
  maxDurationMs?: number;
  /** All-span tag predicates, each pre-encoded as `key:value` (contains) or `key=value` (exact). */
  tags?: string[];
}

export interface TraceHistogramQuery {
  start: Date;
  end: Date;
  bucketCount?: number;
  mode?: string;
  service?: string;
  operation?: string;
  minDurationMs?: number;
  maxDurationMs?: number;
  tags?: string[];
  /** Latency bucket grid dimensions — only read by `getTraceOverview`. */
  latencyTimeCols?: number;
  latencyDurationRows?: number;
  /** Items paging/sort — only read by `getTraceOverview` (list-page-scale plan, Phase 2). */
  sort?: string;
  dir?: 'asc' | 'desc';
  limit?: number;
  offset?: number;
  /** Row cap for `recentErrors`/`slowestTraces` — only read by `getTraceOverview` (dashboard). */
  sampleSize?: number;
}

/** One cell of the trace latency chart's time × log-duration grid (server-computed, Phase 3 of
 *  the trace-latency-p50 plan) — replaces the client-side `binLatencyPoints`/`LatencyBucket`. */
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

/**
 * Same volume histogram as `getTraceHistogram` plus per-service RED stats, the latency bucket
 * grid, and (list-page-scale plan, Phase 2) the traces list page's table rows and the dashboard's
 * recent-errors/slowest-traces samples — all from one backend scan instead of several. Kept as
 * its own endpoint/type rather than an option on `getTraceHistogram` so the traces list page's
 * volume chart never fetches or pays for stats it doesn't read when latency buckets aren't needed
 * either.
 */
export interface TraceOverview {
  buckets: TimeBucket[];
  services: ServiceStats[];
  latencyBuckets?: TraceLatencyBucket[];
  /** The traces list page's table rows — see `TraceHistogramQuery.sort`/`dir`/`limit`/`offset`. */
  items: TraceInfo[];
  /** Total rows matching the filter, before paging — the paginator's "of N" count. */
  total: number;
  /** Dashboard's recent-errors table (newest first, top `sampleSize`). */
  recentErrors: TraceInfo[];
  /** Dashboard's slowest-traces table (duration desc, top `sampleSize`). */
  slowestTraces: TraceInfo[];
}

@Injectable({ providedIn: 'root' })
export class TracesApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiUrl}/traces`;

  getTraces(filter: TraceFilter): Observable<TraceInfo[]> {
    let params = new HttpParams()
      .set('start', filter.start.toISOString())
      .set('end', filter.end.toISOString())
      .set('limit', filter.limit ?? 200)
      .set('mode', filter.mode ?? 'all');
    if (filter.service) params = params.set('service', filter.service);
    if (filter.minDurationMs != null) params = params.set('minDurationMs', filter.minDurationMs);
    return this.http.get<TraceInfo[]>(this.base, { params });
  }

  /** Server-side filtered + paged traces for the traces list page (returns the full filtered total). */
  searchTraces(query: TraceSearchQuery): Observable<PagedResult<TraceInfo>> {
    let params = new HttpParams()
      .set('start', query.start.toISOString())
      .set('end', query.end.toISOString())
      .set('limit', query.limit ?? 100)
      .set('offset', query.offset)
      .set('mode', query.mode ?? 'all');
    if (query.service) params = params.set('service', query.service);
    if (query.minDurationMs != null) params = params.set('minDurationMs', query.minDurationMs);
    if (query.maxDurationMs != null) params = params.set('maxDurationMs', query.maxDurationMs);
    if (query.operation) params = params.set('operation', query.operation);
    if (query.sort) params = params.set('sort', query.sort).set('dir', query.dir ?? 'desc');
    for (const tag of query.tags ?? []) params = params.append('tag', tag);
    return this.http.get<PagedResult<TraceInfo>>(`${this.base}/search`, { params });
  }

  /** True volume histogram (unaffected by any row-count cap) for the traces chart. */
  getTraceHistogram(query: TraceHistogramQuery): Observable<TimeBucket[]> {
    let params = new HttpParams()
      .set('start', query.start.toISOString())
      .set('end', query.end.toISOString())
      .set('bucketCount', query.bucketCount ?? 24)
      .set('mode', query.mode ?? 'all');
    if (query.service) params = params.set('service', query.service);
    if (query.operation) params = params.set('operation', query.operation);
    if (query.minDurationMs != null) params = params.set('minDurationMs', query.minDurationMs);
    if (query.maxDurationMs != null) params = params.set('maxDurationMs', query.maxDurationMs);
    for (const tag of query.tags ?? []) params = params.append('tag', tag);
    return this.http.get<TimeBucket[]>(`${this.base}/histogram`, { params }).pipe(
      map((buckets) => buckets.map((b) => ({ ...b, timestamp: new Date(b.timestamp) })))
    );
  }

  /**
   * Same query shape as `getTraceHistogram`, plus per-service RED stats, (when requested) the
   * latency bucket grid, and — via `sort`/`dir`/`limit`/`offset`/`sampleSize` — the traces list
   * page's table rows and the dashboard's recent-errors/slowest-traces samples.
   */
  getTraceOverview(query: TraceHistogramQuery): Observable<TraceOverview> {
    let params = new HttpParams()
      .set('start', query.start.toISOString())
      .set('end', query.end.toISOString())
      .set('bucketCount', query.bucketCount ?? 24)
      .set('mode', query.mode ?? 'all');
    if (query.service) params = params.set('service', query.service);
    if (query.operation) params = params.set('operation', query.operation);
    if (query.minDurationMs != null) params = params.set('minDurationMs', query.minDurationMs);
    if (query.maxDurationMs != null) params = params.set('maxDurationMs', query.maxDurationMs);
    if (query.latencyTimeCols != null) params = params.set('latencyTimeCols', query.latencyTimeCols);
    if (query.latencyDurationRows != null) params = params.set('latencyDurationRows', query.latencyDurationRows);
    if (query.sort) params = params.set('sort', query.sort).set('dir', query.dir ?? 'desc');
    if (query.limit != null) params = params.set('limit', query.limit);
    if (query.offset != null) params = params.set('offset', query.offset);
    if (query.sampleSize != null) params = params.set('sampleSize', query.sampleSize);
    for (const tag of query.tags ?? []) params = params.append('tag', tag);
    return this.http.get<TraceOverview>(`${this.base}/overview`, { params }).pipe(
      map((o) => ({
        ...o,
        buckets: o.buckets.map((b) => ({ ...b, timestamp: new Date(b.timestamp) })),
        latencyBuckets: o.latencyBuckets?.map((b) => ({ ...b, xStart: new Date(b.xStart), xEnd: new Date(b.xEnd) })),
      }))
    );
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
}
