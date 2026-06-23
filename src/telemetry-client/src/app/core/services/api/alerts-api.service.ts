import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { AlertEvent, AlertRule } from '../../models/alert.models';

@Injectable({ providedIn: 'root' })
export class AlertsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiUrl}/alerts`;

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
