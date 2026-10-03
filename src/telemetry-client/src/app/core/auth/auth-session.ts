import { InjectionToken } from '@angular/core';
import { Observable } from 'rxjs';

/**
 * The SPA's own sign-in, present only in `oidc` mode (provided by `provideOidcAuth`, which is
 * imported on demand so a cookie-mode deployment never downloads the OIDC client). Null in `cookie`
 * mode, where the host owns sign-in and the browser attaches its cookie.
 */
export interface AuthSession {
  /** The current access token (refreshed when needed), or null when there is none. */
  accessToken(): Observable<string | null>;
  /** Starts the sign-in redirect, coming back to the app-relative `returnUrl`. */
  signIn(returnUrl: string): void;
  signOut(): void;
}

export const AUTH_SESSION = new InjectionToken<AuthSession | null>('AUTH_SESSION', {
  providedIn: 'root',
  factory: () => null,
});

const RETURN_URL_KEY = 'telemetry.returnUrl';

/** The current location relative to the UI's base path, in the form `Router.navigateByUrl` takes. */
export function currentAppUrl(): string {
  const base = new URL(document.baseURI).pathname;
  const { pathname, search, hash } = window.location;
  return '/' + pathname.slice(base.length) + search + hash;
}

export function saveReturnUrl(url: string): void {
  try { sessionStorage.setItem(RETURN_URL_KEY, url); } catch { /* unavailable */ }
}

/** Reads and clears the url saved before a sign-in redirect. */
export function takeReturnUrl(): string {
  try {
    const url = sessionStorage.getItem(RETURN_URL_KEY);
    sessionStorage.removeItem(RETURN_URL_KEY);
    // Only ever an app-relative path: never navigate to something a query string could inject.
    if (url && url.startsWith('/') && !url.startsWith('//')) return url;
  } catch { /* unavailable */ }
  return '/';
}
