import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { APP_CONFIG } from '../config/app-config';

/**
 * `GET /api/capabilities` response shape. Schema 3.0.0 removed the provider tier and the
 * indexed-search flag (search is unindexed and window-bounded on every provider).
 */
export interface Capabilities {
  provider: string;
  exemplarPaging: boolean;
  rawSearchWindowHours: number | null;
  exportMaxWindowDays: number;
}

const DEFAULT_CAPABILITIES: Capabilities = {
  provider: 'Unknown',
  exemplarPaging: true,
  rawSearchWindowHours: null,
  exportMaxWindowDays: 7,
};

/**
 * Fetches the active provider's capabilities once at startup and caches them for the rest of the
 * session (list-pages-server-side plan, Phase 2) — every later phase's UI reads this instead of
 * calling `/api/capabilities` itself. Falls back to defaults (no search limit shown) on any
 * failure, since guessing "unlimited" is safer for the UI than guessing "restricted".
 */
@Injectable({ providedIn: 'root' })
export class CapabilitiesService {
  private readonly http = inject(HttpClient);
  private readonly base = inject(APP_CONFIG).apiUrl;

  private readonly _capabilities = signal<Capabilities>(DEFAULT_CAPABILITIES);
  readonly capabilities = this._capabilities.asReadonly();

  private readonly ready: Promise<void>;

  constructor() {
    this.ready = firstValueFrom(this.http.get<Capabilities>(`${this.base}/capabilities`))
      .then((c) => this._capabilities.set(c))
      .catch((err) => console.warn('[capabilities] Failed to load /api/capabilities; using defaults.', err));
  }

  /** Resolves once the initial fetch has settled (success or fallback) — await before reading a limit that must reflect the real provider. */
  whenReady(): Promise<void> {
    return this.ready;
  }
}
