import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { APP_CONFIG } from '../../config/app-config';
import { TenantService } from '../tenant.service';
import { tenantApiUrl } from './tenant-api-url';
import { AlertEvent, AlertRule } from '../../models/alert.models';

@Injectable({ providedIn: 'root' })
export class AlertsApiService {
  private readonly http = inject(HttpClient);
  private readonly tenant = inject(TenantService);
  private readonly apiUrl = inject(APP_CONFIG).apiUrl;
  /** Resolved per call: the tenant is the route's, and changes with it. */
  private get base(): string { return `${tenantApiUrl(this.apiUrl, this.tenant.requireTenantId())}/alerts`; }

  getRules(): Observable<AlertRule[]> {
    return this.http.get<AlertRule[]>(`${this.base}/rules`);
  }

  createRule(rule: Omit<AlertRule, 'id' | 'createdAt' | 'lastFiredAt'>): Observable<AlertRule> {
    return this.http.post<AlertRule>(`${this.base}/rules`, rule);
  }

  updateRule(id: number, rule: AlertRule): Observable<AlertRule> {
    return this.http.put<AlertRule>(`${this.base}/rules/${id}`, rule);
  }

  deleteRule(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/rules/${id}`);
  }

  getEvents(limit = 50): Observable<AlertEvent[]> {
    const params = new HttpParams().set('limit', limit);
    return this.http.get<AlertEvent[]>(`${this.base}/events`, { params });
  }
}
