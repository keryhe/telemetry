import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { APP_CONFIG } from '../config/app-config';
import { AUTH_SESSION, UserInfo } from './auth-session';

/** The answer of `GET {apiUrl}/me` (cookie mode). */
interface MeResponse extends UserInfo {
  signedIn: boolean;
}

/**
 * - `loading`: not asked yet;
 * - `out`: nobody is signed in (dev hosts with no sign-in, or an expired session);
 * - `in`: signed in, details in {@link UserService.user};
 * - `unknown`: the lookup failed, so whether someone is signed in cannot be said.
 */
export type UserState = 'loading' | 'out' | 'in' | 'unknown';

/**
 * Who is signed in, for the header's user menu. OIDC mode reads the session's own user data; cookie mode asks
 * the API's `GET /me` once. Started by the shell (`load()`), not by the constructor: the request goes through the
 * auth interceptor, which injects `AuthService`, which injects this.
 */
@Injectable({ providedIn: 'root' })
export class UserService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = inject(APP_CONFIG).apiUrl;
  private readonly session = inject(AUTH_SESSION);

  readonly state = signal<UserState>('loading');
  /** The details when signed in; null otherwise (and possibly empty when the provider released none). */
  readonly user = signal<UserInfo | null>(null);

  /** Whether the header has a user button: someone is signed in, or might be. */
  readonly signedInOrUnknown = computed(() => this.state() === 'in' || this.state() === 'unknown');

  private started = false;

  load(): void {
    if (this.started) return;
    this.started = true;

    if (this.session) {
      this.session.user().subscribe({
        next: (user) => { this.user.set(user); this.state.set(user ? 'in' : 'out'); },
        error: () => this.state.set('unknown'),
      });
      return;
    }

    this.http.get<MeResponse>(`${this.apiUrl}/me`).subscribe({
      next: (me) => {
        if (!me.signedIn) { this.user.set(null); this.state.set('out'); return; }
        this.user.set({ name: me.name, givenName: me.givenName, email: me.email, picture: me.picture });
        this.state.set('in');
      },
      error: () => this.state.set('unknown'),
    });
  }
}
