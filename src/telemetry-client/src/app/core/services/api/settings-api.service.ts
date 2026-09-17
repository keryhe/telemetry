import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { RetentionSettings } from '../../models/settings.models';

@Injectable({ providedIn: 'root' })
export class SettingsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiUrl}/settings`;

  getRetentionSettings(): Observable<RetentionSettings> {
    return this.http.get<RetentionSettings>(`${this.base}/retention`);
  }

  updateRetentionSettings(settings: RetentionSettings): Observable<RetentionSettings> {
    return this.http.put<RetentionSettings>(`${this.base}/retention`, settings);
  }
}
