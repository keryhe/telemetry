import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { APP_CONFIG } from '../../config/app-config';
import { TenantService } from '../tenant.service';
import { tenantApiUrl } from './tenant-api-url';
import { LogRecord } from '../../models/log.models';

/** Filter shape shared by list/facets/export. */
export interface LogFilter {
  start: Date;
  end: Date;
  service?: string;
  minSeverity?: number;
  q?: string;
}

/** The log summary's query (plans/summary-rollups.md): range, service and minimum severity; search does not apply to it. */
export interface LogSummaryQuery {
  start: Date;
  end: Date;
  service?: string;
  minSeverity?: number;
  bucketCount?: number;
}

export type ListOrder = 'newest' | 'oldest';

export interface LogListQuery extends LogFilter {
  order?: ListOrder;
  /** Fewer rows than the server's limit; omitted asks for as many as it allows. */
  limit?: number;
}

export interface LogFacetsQuery extends LogFilter {
  keys?: string[];
  valueLimit?: number;
}

/** Wire shape of one summary bucket (backend field is `timestamp`, not `time`). */
interface LogSummaryBucketDto {
  timestamp: string;
  coveredSeconds: number;
  trace: number;
  debug: number;
  info: number;
  warn: number;
  error: number;
  fatal: number;
}

export interface LogSummaryBucket {
  time: Date;
  coveredSeconds: number;
  trace: number;
  debug: number;
  info: number;
  warn: number;
  error: number;
  fatal: number;
}

interface LogSummaryDto {
  bucketSeconds: number;
  writtenThrough: string;
  buckets: LogSummaryBucketDto[];
  total: number;
  timedOut: boolean;
}

export interface LogSummaryResult {
  bucketSeconds: number;
  /** Buckets from this instant on are left out: the rollup is not written for them yet. */
  writtenThrough: string;
  buckets: LogSummaryBucket[];
  total: number;
  /** The server ran out of time: buckets is empty (not "no logs") and total is 0. */
  timedOut: boolean;
}

export interface LogListResult {
  items: LogRecord[];
  /** More logs matched than `items` holds. */
  truncated: boolean;
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
  /** The sample scan timed out: `facets` is empty because the answer is unknown, not because there are none. */
  timedOut: boolean;
}

@Injectable({ providedIn: 'root' })
export class LogsApiService {
  private readonly http = inject(HttpClient);
  private readonly tenant = inject(TenantService);
  private readonly apiUrl = inject(APP_CONFIG).apiUrl;
  /** Resolved per call: the tenant is the route's, and changes with it. */
  private get base(): string { return `${tenantApiUrl(this.apiUrl, this.tenant.requireTenantId())}/logs`; }

  /** Chart/stat-card summary: per-severity bucket counts from the log rollup (range, service, minimum severity). */
  getLogSummary(query: LogSummaryQuery): Observable<LogSummaryResult> {
    let params = new HttpParams()
      .set('start', query.start.toISOString())
      .set('end', query.end.toISOString())
      .set('bucketCount', query.bucketCount ?? 60);
    if (query.service) params = params.set('service', query.service);
    if (query.minSeverity != null && query.minSeverity >= 0) params = params.set('minSeverity', query.minSeverity);
    return this.http.get<LogSummaryDto>(`${this.base}/summary`, { params }).pipe(
      map((dto) => ({
        bucketSeconds: dto.bucketSeconds,
        writtenThrough: dto.writtenThrough,
        buckets: dto.buckets.map((b) => ({
          time: new Date(b.timestamp), coveredSeconds: b.coveredSeconds,
          trace: b.trace, debug: b.debug, info: b.info, warn: b.warn, error: b.error, fatal: b.fatal,
        })),
        total: dto.total,
        timedOut: dto.timedOut ?? false,
      }))
    );
  }

  /** The newest (or oldest) logs matching the filters, capped by the server; `truncated` says more matched. */
  getLogList(query: LogListQuery): Observable<LogListResult> {
    let params = this.filterParams(query).set('order', query.order ?? 'newest');
    if (query.limit != null) params = params.set('limit', query.limit);
    return this.http.get<LogListResult>(`${this.base}/list`, { params });
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
   * {@link getLogList} (minus the limit and order — export has no row cap), fetched
   * as a Blob so the auth interceptor still runs (see `downloadBlob`'s doc comment). The
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
    if (query.service) params = params.set('service', query.service);
    if (query.minSeverity != null && query.minSeverity >= 0) params = params.set('minSeverity', query.minSeverity);
    if (query.q) params = params.set('q', query.q);
    return params;
  }
}
