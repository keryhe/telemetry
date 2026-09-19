import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { APP_CONFIG } from '../../config/app-config';
import { TimeBucket } from '../../../shared/utils/chart.utils';

/** One tenant's card on the Global Dashboard. */
export interface GlobalTenantStats {
  tenantId: number;
  tenantName: string;
  traceCount: number;
  errorCount: number;
  /** Window percentiles (ms). 0 when traceCount is 0 — "no data", not a value. */
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
  serviceCount: number;
  /** All log records in the window, every severity. */
  logCount: number;
  /** Error + Fatal log records only. */
  logErrorCount: number;
  /** Null when the tenant has sent nothing within the server's activity lookback. */
  lastSeenUtc: Date | null;
  buckets: TimeBucket[];
  /** The server's query for this tenant threw; the card reads "unknown", not "silent". */
  failed: boolean;
}

export interface GlobalOverview {
  tenants: GlobalTenantStats[];
}

/**
 * Cross-tenant reads for the Global Dashboard. The `tenantInterceptor` still stamps
 * `X-Tenant-Id` on these requests — it is registered globally and has no per-request opt-out —
 * but `/api/global/*` deliberately ignores it, so no client-side suppression is needed.
 */
@Injectable({ providedIn: 'root' })
export class GlobalApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiUrl}/global`;

  /**
   * One request for every tenant's card. Deliberately not N calls to `/traces/overview` from the
   * browser: the server already pays for a scan per tenant, and fanning that out over the wire
   * would add N round trips on top.
   */
  getOverview(start: Date, end: Date, bucketCount = 24): Observable<GlobalOverview> {
    const params = new HttpParams()
      .set('start', start.toISOString())
      .set('end', end.toISOString())
      .set('bucketCount', bucketCount);

    return this.http.get<GlobalOverview>(`${this.base}/overview`, { params }).pipe(
      map((o) => ({
        tenants: o.tenants.map((t) => ({
          ...t,
          lastSeenUtc: t.lastSeenUtc ? new Date(t.lastSeenUtc) : null,
          buckets: t.buckets.map((b) => ({ ...b, timestamp: new Date(b.timestamp) })),
        })),
      }))
    );
  }
}
