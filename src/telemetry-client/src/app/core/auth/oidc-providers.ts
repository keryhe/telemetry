import { EnvironmentProviders, Provider, inject, provideAppInitializer } from '@angular/core';
import { OidcSecurityService, provideAuth } from 'angular-auth-oidc-client';
import { firstValueFrom, map } from 'rxjs';
import { AppConfig } from '../config/app-config';
import { AUTH_SESSION, AuthSession, currentAppUrl, saveReturnUrl } from './auth-session';

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
