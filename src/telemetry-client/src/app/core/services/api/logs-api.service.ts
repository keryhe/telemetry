import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { APP_CONFIG } from '../../config/app-config';
import { LogRecord } from '../../models/log.models';

/** Filter shape shared by summary/page/facets (list-pages-server-side plan, Phase 2 Target API). */
export interface LogFilter {
  start: Date;
  end: Date;
  // Opaque and never parsed into a Date (decision 3: "returned to the client opaquely and never
  // converted"): the server's asOf carries sub-millisecond precision that a JS Date can't hold,
  // so round-tripping it through `new Date(...)`/`.toISOString()` silently truncates it and
  // breaks the keyset cursor's filter-hash check on the very next page/next/prev/last request.
  asOf?: string;
  service?: string;
  minSeverity?: number;
  q?: string;
}

export interface LogSummaryQuery extends LogFilter {
  bucketCount?: number;
}

export interface LogPageQuery extends LogFilter {
  size: number;
  cursor?: string;
  nav?: 'first' | 'next' | 'prev' | 'last';
}

export interface LogFacetsQuery extends LogFilter {
  keys?: string[];
  valueLimit?: number;
}

/** Wire shape of one summary bucket (backend field is `timestamp`, not `time`). */
interface LogSummaryBucketDto {
  timestamp: string;
  trace: number;
  debug: number;
  info: number;
  warn: number;
  error: number;
  fatal: number;
}

export interface LogSummaryBucket {
  time: Date;
  trace: number;
  debug: number;
  info: number;
  warn: number;
  error: number;
  fatal: number;
}

interface LogSummaryDto {
  source: 'rollup' | 'raw';
  buckets: LogSummaryBucketDto[];
  total: number;
  totalIsLowerBound: boolean;
  asOf: string;
}

export interface LogSummaryResult {
  source: 'rollup' | 'raw';
  buckets: LogSummaryBucket[];
  total: number;
  totalIsLowerBound: boolean;
  asOf: string;
}

interface LogPageDto {
  items: LogRecord[];
  nextCursor: string | null;
  prevCursor: string | null;
  asOf: string;
}

export interface LogPageResult {
  items: LogRecord[];
  nextCursor: string | null;
  prevCursor: string | null;
  asOf: string;
}

export interface LogFacetValue {
  value: string;
  count: number;
}

export interface LogFacet {
  key: string;
  values: LogFacetValue[];
}

export interface LogFacetsResult {
  sampleSize: number;
  facets: LogFacet[];
}

@Injectable({ providedIn: 'root' })
export class LogsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiUrl}/logs`;

  getLogs(start: Date, end: Date): Observable<LogRecord[]> {
    const params = new HttpParams()
      .set('start', start.toISOString())
      .set('end', end.toISOString());
    return this.http.get<LogRecord[]>(this.base, { params });
  }

  /** Chart/stat-card summary — the per-severity bucket counts (decisions 3, 37). */
  getLogSummary(query: LogSummaryQuery): Observable<LogSummaryResult> {
    let params = this.filterParams(query).set('bucketCount', query.bucketCount ?? 60);
    return this.http.get<LogSummaryDto>(`${this.base}/summary`, { params }).pipe(
      map((dto) => ({
        source: dto.source,
        buckets: dto.buckets.map((b) => ({
          time: new Date(b.timestamp),
          trace: b.trace, debug: b.debug, info: b.info, warn: b.warn, error: b.error, fatal: b.fatal,
        })),
        total: dto.total,
        totalIsLowerBound: dto.totalIsLowerBound,
        asOf: dto.asOf,
      }))
    );
  }

  /** Keyset-paged log rows (decision 1), pinned on `asOf` (decision 3). */
  getLogPage(query: LogPageQuery): Observable<LogPageResult> {
    let params = this.filterParams(query).set('size', query.size).set('nav', query.nav ?? 'first');
    if (query.cursor) params = params.set('cursor', query.cursor);
    return this.http.get<LogPageDto>(`${this.base}/page`, { params }).pipe(
      map((dto) => ({
        items: dto.items,
        nextCursor: dto.nextCursor,
        prevCursor: dto.prevCursor,
        asOf: dto.asOf,
      }))
    );
  }

  /** Server-side attribute facets over the newest matching rows (decision 15). */
  getLogFacets(query: LogFacetsQuery): Observable<LogFacetsResult> {
    let params = this.filterParams(query);
    if (query.keys?.length) params = params.set('keys', query.keys.join(','));
    if (query.valueLimit != null) params = params.set('valueLimit', query.valueLimit);
    return this.http.get<LogFacetsResult>(`${this.base}/facets`, { params });
  }

  getLogsByTrace(traceId: string): Observable<LogRecord[]> {
    return this.http.get<LogRecord[]>(`${this.base}/by-trace/${traceId}`);
  }

  /**
   * Streaming export (list-pages-server-side plan, Phase 8): the same filters as
   * {@link getLogSummary}/{@link getLogPage} (minus `asOf`/paging — export has no row cap), fetched
   * as a Blob so the `X-Tenant-Id` interceptor still runs (see `downloadBlob`'s doc comment). The
   * caller (logs.component.ts's Export menu) hands the result straight to `downloadBlob`.
   */
  getLogExport(query: LogFilter, format: 'ndjson' | 'csv'): Observable<Blob> {
    const params = this.filterParams(query).set('format', format);
    return this.http.get(`${this.base}/export`, { params, responseType: 'blob' });
  }

  /** Logs immediately before/after an anchor timestamp for one service, ignoring the active filters. */
  getLogContext(anchorTimeUnixNano: number, service: string | undefined, before = 10, after = 10): Observable<LogRecord[]> {
    let params = new HttpParams()
      .set('anchor', anchorTimeUnixNano)
      .set('before', before)
      .set('after', after);
    if (service) params = params.set('service', service);
    return this.http.get<LogRecord[]>(`${this.base}/context`, { params });
  }

  private filterParams(query: LogFilter): HttpParams {
    let params = new HttpParams()
      .set('start', query.start.toISOString())
      .set('end', query.end.toISOString());
    if (query.asOf) params = params.set('asOf', query.asOf);
    if (query.service) params = params.set('service', query.service);
    if (query.minSeverity != null && query.minSeverity >= 0) params = params.set('minSeverity', query.minSeverity);
    if (query.q) params = params.set('q', query.q);
    return params;
  }
}
