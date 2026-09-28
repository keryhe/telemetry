import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { APP_CONFIG } from '../../config/app-config';
import {
  MetricCatalogPage,
  MetricCatalogQueryParams,
  MetricExemplarPage,
  MetricExemplarQueryParams,
  MetricInfo,
  MetricSeriesQueryParams,
  MetricSeriesResult,
  MetricsSummary,
} from '../../models/metric.models';

@Injectable({ providedIn: 'root' })
export class MetricsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiUrl}/metrics`;

  /** Server-paged metrics catalog (Phase 5): replaces the former unbounded getAllMetrics call. */
  getCatalog(p: MetricCatalogQueryParams): Observable<MetricCatalogPage> {
    let params = new HttpParams()
      .set('start', p.start.toISOString())
      .set('end', p.end.toISOString())
      .set('groupBy', p.groupBy);
    if (p.q) params = params.set('q', p.q);
    if (p.service) params = params.set('service', p.service);
    if (p.type != null) params = params.set('type', p.type);
    if (p.size != null) params = params.set('size', p.size);
    if (p.cursor) params = params.set('cursor', p.cursor);
    if (p.nav) params = params.set('nav', p.nav);
    return this.http.get<MetricCatalogPage>(`${this.base}/catalog`, { params });
  }

  /** True unique-metric-name-per-type counts over the full range, unaffected by the catalog's page size. */
  getMetricsSummary(start?: Date, end?: Date): Observable<MetricsSummary> {
    let params = new HttpParams();
    if (start) params = params.set('start', start.toISOString());
    if (end) params = params.set('end', end.toISOString());
    return this.http.get<MetricsSummary>(`${this.base}/summary`, { params });
  }

  getByName(name: string): Observable<MetricInfo[]> {
    return this.http.get<MetricInfo[]>(`${this.base}/by-name/${encodeURIComponent(name)}`);
  }

  getLabels(name: string, start: Date, end: Date): Observable<{ partial: boolean; labels: Record<string, string[]> }> {
    const params = new HttpParams().set('start', start.toISOString()).set('end', end.toISOString());
    return this.http.get<{ partial: boolean; labels: Record<string, string[]> }>(
      `${this.base}/labels/${encodeURIComponent(name)}`,
      { params },
    );
  }

  /** Database-bucketed series (Phase 4): one query, pre-aggregated per type, top-N + "other" folded server-side. */
  getSeries(p: MetricSeriesQueryParams): Observable<MetricSeriesResult> {
    let params = this.filterParams(p).set('points', p.points);
    if (p.top != null) params = params.set('top', p.top);
    return this.http.get<MetricSeriesResult>(`${this.base}/series`, { params });
  }

  /** Tier-aware exemplars (Phase 4): real keyset paging on the analytics tier, newest-500 capped on standard. */
  getExemplars(p: MetricExemplarQueryParams): Observable<MetricExemplarPage> {
    let params = this.filterParams(p);
    if (p.size != null) params = params.set('size', p.size);
    if (p.cursor) params = params.set('cursor', p.cursor);
    if (p.nav) params = params.set('nav', p.nav);
    return this.http.get<MetricExemplarPage>(`${this.base}/exemplars`, { params });
  }

  private filterParams(p: { metricName: string; start: Date; end: Date; metricId?: number; labelFilters?: Record<string, string>; q?: string }): HttpParams {
    let params = new HttpParams()
      .set('metricName', p.metricName)
      .set('start', p.start.toISOString())
      .set('end', p.end.toISOString());
    if (p.metricId != null) params = params.set('metricId', p.metricId);
    if (p.q) params = params.set('q', p.q);
    if (p.labelFilters) {
      for (const [k, v] of Object.entries(p.labelFilters)) {
        params = params.append('labelFilter', `${k}:${v}`);
      }
    }
    return params;
  }
}
