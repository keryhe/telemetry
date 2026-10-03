import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, take, throwError } from 'rxjs';
import { AUTH_SESSION } from '../auth/auth-session';
import { AuthService } from '../auth/auth.service';
import { APP_CONFIG } from '../config/app-config';
import { TenantService } from '../services/tenant.service';

/** The header the API's CSRF rule asks for on a change that carries no bearer token. */
export const CLIENT_HEADER = 'X-Telemetry-Client';

/** Set to `tenant` by the API on a 403 refused by the tenant check (not by the operation check). */
const DENIED_HEADER = 'X-Telemetry-Denied';

/**
 * Applies to calls to the API only (URLs under `apiUrl`): never to anything else the page loads,
 * so a token is never sent to a third party.
 *  - always marks the call as ours (`X-Telemetry-Client`);
 *  - `oidc` mode attaches the access token; `cookie` mode with `includeCredentials` sends cookies;
 *  - a 401 starts sign-in (see AuthService); a 403 the API marks as a tenant refusal
 *    (`X-Telemetry-Denied: tenant`) marks that tenant as refused, which the shell shows as "no access to
 *    this tenant" rather than a page of failing requests. Any other 403 (an operation the caller may not
 *    perform, such as saving an alert rule or exporting) is left to the caller: the tenant stays usable.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const { apiUrl, auth } = inject(APP_CONFIG);
  if (req.url !== apiUrl && !req.url.startsWith(apiUrl + '/')) return next(req);

  const session = inject(AUTH_SESSION);
  const authService = inject(AuthService);
  const tenants = inject(TenantService);

  const request = req.clone({
    setHeaders: { [CLIENT_HEADER]: '1' },
    ...(auth.includeCredentials ? { withCredentials: true } : {}),
  });

  const send = session
    ? session.accessToken().pipe(
        take(1),
        switchMap((token) => next(token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request)))
    : next(request);

  return send.pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse) {
        if (error.status === 401) authService.handleUnauthorized();
        else if (error.status === 403 && error.headers.get(DENIED_HEADER) === 'tenant') {
          const match = /\/tenants\/(\d+)(\/|$|\?)/.exec(req.url.slice(apiUrl.length));
          if (match) tenants.markForbidden(Number(match[1]));
        }
      }
      return throwError(() => error);
    }));
};
