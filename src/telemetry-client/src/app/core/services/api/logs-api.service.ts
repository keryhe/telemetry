import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { LogRecord } from '../../models/log.models';
import { PagedResult } from '../../models/paged.models';

export interface LogSearchQuery {
  start: Date;
  end: Date;
  service?: string;
  minSeverity?: number;
  q?: string;
  limit: number;
  offset: number;
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

  getLogsByTrace(traceId: string): Observable<LogRecord[]> {
    return this.http.get<LogRecord[]>(`${this.base}/by-trace/${traceId}`);
  }

  getServices(start?: Date, end?: Date): Observable<string[]> {
    let params = new HttpParams();
    if (start) params = params.set('start', start.toISOString());
    if (end)   params = params.set('end',   end.toISOString());
    return this.http.get<string[]>(`${this.base}/services`, { params });
  }
}
