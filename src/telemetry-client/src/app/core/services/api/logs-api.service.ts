import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { LogRecord } from '../../models/log.models';

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

  getLogsByTrace(traceId: string): Observable<LogRecord[]> {
    return this.http.get<LogRecord[]>(`${this.base}/by-trace/${traceId}`);
  }
}
