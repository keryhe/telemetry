import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { ServiceDependency, SpanModel, TraceFilter, TraceInfo } from '../../models/trace.models';

@Injectable({ providedIn: 'root' })
export class TracesApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiUrl}/traces`;

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

  getSpans(traceId: string): Observable<SpanModel[]> {
    return this.http.get<SpanModel[]>(`${this.base}/${traceId}/spans`);
  }

  getServices(start?: Date, end?: Date): Observable<string[]> {
    let params = new HttpParams();
    if (start) params = params.set('start', start.toISOString());
    if (end) params = params.set('end', end.toISOString());
    return this.http.get<string[]>(`${this.base}/services`, { params });
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
}
