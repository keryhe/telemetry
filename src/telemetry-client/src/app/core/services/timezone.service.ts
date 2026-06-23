import { Injectable } from '@angular/core';

/**
 * Records the browser's IANA timezone so the server can convert UTC timestamps
 * to local time when needed. Mirrors Blazor's MainLayout timezone capture.
 */
@Injectable({ providedIn: 'root' })
export class TimezoneService {
  /** e.g. "America/New_York"; falls back to "UTC" if unavailable. */
  readonly timeZone: string = (() => {
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
    } catch {
      return 'UTC';
    }
  })();
}
