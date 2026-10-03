import { inject } from '@angular/core';
import { ActivatedRouteSnapshot, CanActivateFn, CanDeactivateFn, Router } from '@angular/router';
import { TenantService } from '../services/tenant.service';

/**
 * On `t/:tenantId`: makes the route's tenant the current one *before* any page component exists,
 * so a page's first API call already targets it. Re-runs when only the id changes
 * (`runGuardsAndResolvers: 'paramsChange'`). A malformed id goes back to the default redirect.
 */
export const tenantRouteGuard: CanActivateFn = (route) => {
  const id = Number(route.paramMap.get('tenantId'));
  if (!Number.isInteger(id) || id <= 0) return inject(Router).parseUrl('/');
  inject(TenantService).enterTenantRoute(id);
  return true;
};

/** Leaving the tenant routes (for settings, say) means there is no route tenant any more. */
export const tenantRouteLeaveGuard: CanDeactivateFn<unknown> = (_c, _cur, _state, next) => {
  // Staying under /t/... keeps the tenant; the next tenantRouteGuard run replaces it.
  if (!next.url.startsWith('/t/')) inject(TenantService).leaveTenantRoute();
  return true;
};

/**
 * `''` and the tenant-less paths of earlier versions (`/traces?range=1h`, bookmarks) redirect to the
 * same path and query under the last used tenant (else the first permitted one): `/traces/abc` goes
 * to `/t/3/traces/abc`.
 */
export const legacyTenantRedirect: CanActivateFn = async (route: ActivatedRouteSnapshot) => {
  const router = inject(Router);
  const id = await inject(TenantService).defaultTenantId();
  if (id === null) return router.createUrlTree(['/no-tenants']);
  const segments = route.url.map((s) => s.path);
  return router.createUrlTree(['/t', id, ...(segments.length ? segments : ['dashboard'])], {
    queryParams: route.queryParams,
    fragment: route.fragment ?? undefined,
  });
};
