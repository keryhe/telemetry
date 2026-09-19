import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { APP_CONFIG } from '../../config/app-config';

/**
 * Signal-agnostic reads over `resources` directly — not joined through spans/metrics/log_records.
 * The single authoritative "available services" list for the current tenant, independent of
 * signal type or time range.
 */
@Injectable({ providedIn: 'root' })
export class ResourcesApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiUrl}/resources`;

  getServices(): Observable<string[]> {
    return this.http.get<string[]>(`${this.base}/services`);
  }
}
