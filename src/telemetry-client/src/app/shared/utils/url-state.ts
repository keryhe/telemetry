import { inject, Injectable } from '@angular/core';
import { NavigationEnd, Params, Router } from '@angular/router';
import { Observable, filter, map, startWith } from 'rxjs';

/**
 * App-wide query-param mirror. Pages use this to keep their filter/paging state in the URL so any
 * view is shareable and deep-linkable (Jaeger/Grafana/Kibana-style). Reads win over localStorage on
 * load; writes merge into the current URL without adding history entries per keystroke.
 *
 * Kept deliberately tiny: it wraps the Router so components (and the app-wide TimeRangeService) can
 * read the current params and patch a subset without each re-implementing merge/replace semantics.
 */
@Injectable({ providedIn: 'root' })
export class UrlStateService {
  private readonly router = inject(Router);

  /** Current query params snapshot (parsed from the live URL, independent of route depth). */
  read(): Params {
    return this.router.parseUrl(this.router.url).queryParams ?? {};
  }

  /** A single query param value, or null when absent. */
  get(key: string): string | null {
    const v = this.read()[key];
    return v == null ? null : String(v);
  }

  /** Emits the query params on every navigation (including back/forward), starting with the current set. */
  changes(): Observable<Params> {
    return this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map(() => this.read()),
      startWith(this.read()),
    );
  }

  /**
   * Merge `params` into the current URL query string. Keys mapped to null/undefined/'' are removed.
   * Uses `replaceUrl` so rapid edits (typing, paging) don't flood the browser history; navigations
   * that should be undoable (a fresh preset, say) can pass `{ replace: false }`.
   */
  patch(params: Record<string, string | number | null | undefined>, opts: { replace?: boolean } = {}): void {
    const current = this.read();
    const next: Params = { ...current };
    let changed = false;

    for (const [key, raw] of Object.entries(params)) {
      const value = raw === null || raw === undefined || raw === '' ? undefined : String(raw);
      if (value === undefined) {
        if (key in next) { delete next[key]; changed = true; }
      } else if (next[key] !== value) {
        next[key] = value; changed = true;
      }
    }

    if (!changed) return;
    this.router.navigate([], {
      queryParams: next,
      replaceUrl: opts.replace ?? true,
    });
  }
}
