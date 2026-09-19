import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { APP_CONFIG } from '../../config/app-config';
import { Tenant } from '../../models/alert.models';

@Injectable({ providedIn: 'root' })
export class TenantsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = inject(APP_CONFIG).apiUrl;

  getTenants(): Observable<Tenant[]> {
    return this.http.get<Tenant[]>(`${this.base}/tenants`);
  }
}
