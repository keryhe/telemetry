import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { APP_CONFIG } from '../../config/app-config';
import { TenantService } from '../tenant.service';
import { tenantApiUrl } from './tenant-api-url';

/**
 * Signal-agnostic reads over `resources` directly — not joined through spans/metrics/log_records.
 * The single authoritative "available services" list for the current tenant, independent of
 * signal type or time range.
 */
@Injectable({ providedIn: 'root' })
export class ResourcesApiService {
  private readonly http = inject(HttpClient);
  private readonly tenant = inject(TenantService);
  private readonly apiUrl = inject(APP_CONFIG).apiUrl;
  /** Resolved per call: the tenant is the route's, and changes with it. */
  private get base(): string { return `${tenantApiUrl(this.apiUrl, this.tenant.requireTenantId())}/resources`; }

  getServices(): Observable<string[]> {
    return this.http.get<string[]>(`${this.base}/services`);
  }
}
