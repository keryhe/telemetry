import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { TenantService } from '../services/tenant.service';

export const tenantInterceptor: HttpInterceptorFn = (req, next) => {
  const tenant = inject(TenantService).selectedTenant();
  if (!tenant) return next(req);
  return next(req.clone({ headers: req.headers.set('X-Tenant-Id', String(tenant.id)) }));
};
