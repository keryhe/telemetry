import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { APP_CONFIG } from '../../config/app-config';
import { TenantService } from '../tenant.service';
import { tenantApiUrl } from './tenant-api-url';
import { RetentionSettings } from '../../models/settings.models';

/** A tenant's settings: today its retention windows (there is no global retention default). */
@Injectable({ providedIn: 'root' })
export class SettingsApiService {
  private readonly http = inject(HttpClient);
  private readonly tenant = inject(TenantService);
  private readonly apiUrl = inject(APP_CONFIG).apiUrl;
  /** Resolved per call: the tenant is the route's, and changes with it. */
  private get base(): string { return `${tenantApiUrl(this.apiUrl, this.tenant.requireTenantId())}/settings`; }

  getRetentionSettings(): Observable<RetentionSettings> {
    return this.http.get<RetentionSettings>(`${this.base}/retention`);
  }

  updateRetentionSettings(settings: RetentionSettings): Observable<RetentionSettings> {
    return this.http.put<RetentionSettings>(`${this.base}/retention`, settings);
  }
}
