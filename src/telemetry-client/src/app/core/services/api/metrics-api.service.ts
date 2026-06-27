import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import {
  MetricInfo,
  MetricSeries,
  MetricSeriesParams,
  MultiSeriesMetricData,
} from '../../models/metric.models';

@Injectable({ providedIn: 'root' })
export class MetricsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiUrl}/metrics`;

  getAllMetrics(start?: Date, end?: Date, limit = 500): Observable<MetricInfo[]> {
    let params = new HttpParams().set('limit', limit);
    if (start) params = params.set('start', start.toISOString());
    if (end) params = params.set('end', end.toISOString());
    return this.http.get<MetricInfo[]>(this.base, { params });
  }

  getByName(name: string): Observable<MetricInfo[]> {
    return this.http.get<MetricInfo[]>(`${this.base}/by-name/${encodeURIComponent(name)}`);
  }

  getLabels(name: string): Observable<Record<string, string[]>> {
    return this.http.get<Record<string, string[]>>(`${this.base}/labels/${encodeURIComponent(name)}`);
  }

  getSeries(p: MetricSeriesParams): Observable<MetricSeries> {
    let params = new HttpParams().set('metricName', p.metricName);
    if (p.start) params = params.set('start', p.start.toISOString());
    if (p.end) params = params.set('end', p.end.toISOString());
    if (p.metricId != null) params = params.set('metricId', p.metricId);
    if (p.labelFilters) {
      for (const [k, v] of Object.entries(p.labelFilters)) {
        params = params.append('labelFilter', `${k}:${v}`);
      }
    }
    return this.http.get<MetricSeries>(`${this.base}/series`, { params });
  }

  getGroupedSeries(p: MetricSeriesParams): Observable<MultiSeriesMetricData> {
    let params = new HttpParams().set('metricName', p.metricName);
    if (p.start) params = params.set('start', p.start.toISOString());
    if (p.end) params = params.set('end', p.end.toISOString());
    if (p.metricId != null) params = params.set('metricId', p.metricId);
    if (p.labelFilters) {
      for (const [k, v] of Object.entries(p.labelFilters)) {
        params = params.append('labelFilter', `${k}:${v}`);
      }
    }
    return this.http.get<MultiSeriesMetricData>(`${this.base}/series-grouped`, { params });
  }

  getServices(start?: Date, end?: Date): Observable<string[]> {
    let params = new HttpParams();
    if (start) params = params.set('start', start.toISOString());
    if (end)   params = params.set('end',   end.toISOString());
    return this.http.get<string[]>(`${this.base}/services`, { params });
  }

  getSeriesByService(metricName: string, start?: Date, end?: Date): Observable<MultiSeriesMetricData> {
    let params = new HttpParams();
    if (start) params = params.set('start', start.toISOString());
    if (end) params = params.set('end', end.toISOString());
    return this.http.get<MultiSeriesMetricData>(
      `${this.base}/series-by-service/${encodeURIComponent(metricName)}`,
      { params }
    );
  }
}
