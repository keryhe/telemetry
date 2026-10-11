import { EnvironmentProviders, Provider, inject, provideAppInitializer } from '@angular/core';
import { OidcSecurityService, provideAuth } from 'angular-auth-oidc-client';
import { firstValueFrom, map } from 'rxjs';
import { AppConfig } from '../config/app-config';
import { AUTH_SESSION, AuthSession, UserInfo, currentAppUrl, saveReturnUrl } from './auth-session';

/**
 * `oidc` mode: sign-in by authorization code + PKCE with refresh-token renewal (no iframe), against
 * any OIDC provider. Imported on demand by `main.ts`, so cookie-mode deployments do not carry it.
 *
 * Nothing else loads before the user is signed in: the initializer completes the redirect callback,
 * and when there is no session it starts the sign-in redirect and never resolves.
 */
export function provideOidcAuth(config: AppConfig): (Provider | EnvironmentProviders)[] {
  const oidc = config.auth.oidc!;
  const callback = new URL('callback', document.baseURI).href;
  return [
    provideAuth({
      config: {
        authority: oidc.authority,
        clientId: oidc.clientId,
        scope: oidc.scope,
        responseType: 'code',
        redirectUrl: callback,
        postLogoutRedirectUri: new URL('./', document.baseURI).href,
        silentRenew: true,
        useRefreshToken: true,
        renewTimeBeforeTokenExpiresInSeconds: 30,
        // The API is not reached through the library's interceptor; authInterceptor attaches the token.
        secureRoutes: [],
      },
    }),
    {
      provide: AUTH_SESSION,
      useFactory: (): AuthSession => {
        const service = inject(OidcSecurityService);
        return {
          user: () => service.userData$.pipe(map((r) => toUserInfo(r.userData))),
          accessToken: () => service.getAccessToken().pipe(map((t) => t || null)),
          signIn: (returnUrl) => { saveReturnUrl(returnUrl); service.authorize(); },
          signOut: () => { service.logoff().subscribe(); },
        };
      },
    },
    provideAppInitializer(async () => {
      const service = inject(OidcSecurityService);
      const result = await firstValueFrom(service.checkAuth());
      if (!result.isAuthenticated) {
        saveReturnUrl(currentAppUrl());
        service.authorize();
        await new Promise<void>(() => { /* the page is navigating away */ });
      }
    }),
  ];
}

/** The claims the header uses (`openid profile email`); anything else the provider sends is ignored. */
function toUserInfo(claims: unknown): UserInfo | null {
  if (claims === null || typeof claims !== 'object') return null;
  const c = claims as Record<string, unknown>;
  const text = (v: unknown) => (typeof v === 'string' && v.trim() !== '' ? v.trim() : undefined);
  const email = text(c['email']);
  const username = text(c['preferred_username']);
  return {
    name: text(c['name']),
    givenName: text(c['given_name']),
    email: email ?? (username?.includes('@') ? username : undefined),
    picture: text(c['picture']),
  };
}
