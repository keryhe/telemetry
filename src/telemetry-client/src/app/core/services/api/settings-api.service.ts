import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { APP_CONFIG } from '../../config/app-config';
import { RetentionSettings } from '../../models/settings.models';

@Injectable({ providedIn: 'root' })
export class SettingsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiUrl}/settings`;

  getRetentionSettings(): Observable<RetentionSettings> {
    return this.http.get<RetentionSettings>(`${this.base}/retention`);
  }

  updateRetentionSettings(settings: RetentionSettings): Observable<RetentionSettings> {
    return this.http.put<RetentionSettings>(`${this.base}/retention`, settings);
  }
}
