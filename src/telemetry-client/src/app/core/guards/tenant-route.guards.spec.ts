import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { APP_CONFIG, AppConfig, DEFAULT_APP_CONFIG } from '../config/app-config';
import { TenantsApiService } from '../services/api/tenants-api.service';
import { tenantRouteGuard } from './tenant-route.guards';

describe('tenantRouteGuard', () => {
  const run = (config: Partial<AppConfig>, url: string) => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: APP_CONFIG, useValue: { ...DEFAULT_APP_CONFIG, ...config } },
        { provide: TenantsApiService, useValue: { getTenants: () => of([]) } },
      ],
    });
    const tenantId = url.split('/')[2].split(/[?#]/)[0];
    const route = { paramMap: convertToParamMap({ tenantId }) } as ActivatedRouteSnapshot;
    const result = TestBed.runInInjectionContext(() => tenantRouteGuard(route, { url } as RouterStateSnapshot));
    return result instanceof UrlTree ? TestBed.inject(Router).serializeUrl(result) : result;
  };

  it('sends another tenant\'s page to the same page under the fixed tenant, keeping query and fragment', () => {
    expect(run({ fixedTenantId: 3 }, '/t/7/traces/abc?range=1h#x')).toBe('/t/3/traces/abc?range=1h#x');
  });

  it('lets the fixed tenant\'s own pages through', () => {
    expect(run({ fixedTenantId: 3 }, '/t/3/logs')).toBeTrue();
  });

  it('lets any tenant through when none is fixed', () => {
    expect(run({}, '/t/7/traces')).toBeTrue();
  });

  it('still sends a malformed id to the default redirect', () => {
    expect(run({ fixedTenantId: 3 }, '/t/abc/traces')).toBe('/');
  });
});
