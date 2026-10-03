import { Injectable, inject, signal } from '@angular/core';
import { APP_CONFIG } from '../config/app-config';
import { AUTH_SESSION, currentAppUrl } from './auth-session';

/**
 * What the UI does about authentication. In `cookie` mode the host owns sign-in, so this only
 * redirects to `loginUrl` on a 401; in `oidc` mode it asks the SPA's own session.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly auth = inject(APP_CONFIG).auth;
  private readonly session = inject(AUTH_SESSION);

  /** A request was refused with 401 and nothing could sign the user in (cookie mode without a loginUrl). */
  readonly notSignedIn = signal(false);

  /** Whether the header shows "Sign out". */
  readonly canSignOut = this.session !== null || !!this.auth.logoutUrl;

  private redirecting = false;

  /** Called for a 401 from the API. */
  handleUnauthorized(): void {
    if (this.redirecting) return;
    if (this.session) {
      this.redirecting = true;
      this.session.signIn(currentAppUrl());
    } else if (this.auth.loginUrl) {
      this.redirecting = true;
      window.location.assign(withReturnUrl(this.auth.loginUrl, currentAppUrl()));
    } else {
      this.notSignedIn.set(true);
    }
  }

  signOut(): void {
    if (this.session) this.session.signOut();
    else if (this.auth.logoutUrl) window.location.assign(this.auth.logoutUrl);
  }
}

function withReturnUrl(loginUrl: string, appUrl: string): string {
  // The host's login page returns to a path on its own origin: the UI's path including its base.
  const base = new URL(document.baseURI).pathname.replace(/\/$/, '');
  const returnUrl = encodeURIComponent(base + appUrl);
  return loginUrl + (loginUrl.includes('?') ? '&' : '?') + 'returnUrl=' + returnUrl;
}
