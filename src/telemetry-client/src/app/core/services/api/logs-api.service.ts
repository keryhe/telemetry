import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { environment } from '../../../../environments/environment';
import { LogRecord } from '../../models/log.models';
import { PagedResult } from '../../models/paged.models';
import { LogBucket } from '../../../shared/utils/chart.utils';

export interface LogSearchQuery {
  start: Date;
  end: Date;
  service?: string;
  minSeverity?: number;
  q?: string;
  limit: number;
  offset: number;
}

export interface LogHistogramQuery {
  start: Date;
  end: Date;
  bucketCount?: number;
  service?: string;
  minSeverity?: number;
  q?: string;
}

/** Wire shape of a log histogram bucket (backend field is `timestamp`, not `time`). */
interface LogVolumeBucketDto {
  timestamp: string;
  trace: number;
  debug: number;
  info: number;
  warn: number;
  error: number;
  fatal: number;
}

@Injectable({ providedIn: 'root' })
export class LogsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiUrl}/logs`;

  getLogs(start: Date, end: Date): Observable<LogRecord[]> {
    const params = new HttpParams()
      .set('start', start.toISOString())
      .set('end', end.toISOString());
    return this.http.get<LogRecord[]>(this.base, { params });
  }

  /** Server-side filtered + paged logs for the logs list page. */
  searchLogs(query: LogSearchQuery): Observable<PagedResult<LogRecord>> {
    let params = new HttpParams()
      .set('start', query.start.toISOString())
      .set('end', query.end.toISOString())
      .set('limit', query.limit)
      .set('offset', query.offset);
    if (query.service) params = params.set('service', query.service);
    if (query.minSeverity != null && query.minSeverity >= 0) params = params.set('minSeverity', query.minSeverity);
    if (query.q) params = params.set('q', query.q);
    return this.http.get<PagedResult<LogRecord>>(`${this.base}/search`, { params });
  }

  /** True volume-by-severity histogram (unaffected by any row-count cap) for the logs chart. */
  getLogHistogram(query: LogHistogramQuery): Observable<LogBucket[]> {
    let params = new HttpParams()
      .set('start', query.start.toISOString())
      .set('end', query.end.toISOString())
      .set('bucketCount', query.bucketCount ?? 24);
    if (query.service) params = params.set('service', query.service);
    if (query.minSeverity != null && query.minSeverity >= 0) params = params.set('minSeverity', query.minSeverity);
    if (query.q) params = params.set('q', query.q);
    return this.http.get<LogVolumeBucketDto[]>(`${this.base}/histogram`, { params }).pipe(
      map((buckets) => buckets.map((b) => ({
        time: new Date(b.timestamp),
        trace: b.trace, debug: b.debug, info: b.info, warn: b.warn, error: b.error, fatal: b.fatal,
      })))
    );
  }

  getLogsByTrace(traceId: string): Observable<LogRecord[]> {
    return this.http.get<LogRecord[]>(`${this.base}/by-trace/${traceId}`);
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

  getServices(start?: Date, end?: Date): Observable<string[]> {
    let params = new HttpParams();
    if (start) params = params.set('start', start.toISOString());
    if (end)   params = params.set('end',   end.toISOString());
    return this.http.get<string[]>(`${this.base}/services`, { params });
  }
}
