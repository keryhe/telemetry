import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';

/**
 * Signal-agnostic reads over `resources` directly — not joined through spans/metrics/log_records.
 * The single authoritative "available services" list for the current tenant, independent of
 * signal type or time range.
 */
@Injectable({ providedIn: 'root' })
export class ResourcesApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiUrl}/resources`;

  getServices(): Observable<string[]> {
    return this.http.get<string[]>(`${this.base}/services`);
  }
}
