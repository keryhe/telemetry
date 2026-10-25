import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { APP_CONFIG, AppConfig, DEFAULT_APP_CONFIG } from '../config/app-config';
import { TenantsApiService } from './api/tenants-api.service';
import { TenantService } from './tenant.service';

describe('TenantService on a single-tenant install', () => {
  const setup = (config: Partial<AppConfig>, tenants = [{ id: 1, name: 'one' }, { id: 3, name: 'three' }]) => {
    TestBed.configureTestingModule({
      providers: [
        { provide: APP_CONFIG, useValue: { ...DEFAULT_APP_CONFIG, ...config } },
        { provide: TenantsApiService, useValue: { getTenants: () => of(tenants) } },
      ],
    });
    return TestBed.inject(TenantService);
  };

  let saved: string | null;
  beforeEach(() => {
    saved = localStorage.getItem('selectedTenantId');
    localStorage.setItem('selectedTenantId', '1');
  });
  afterEach(() => (saved === null ? localStorage.removeItem('selectedTenantId') : localStorage.setItem('selectedTenantId', saved)));

  it('makes the fixed tenant the effective and default one whatever localStorage and the list order say', async () => {
    const service = setup({ fixedTenantId: 3 });

    expect(service.isFixed).toBeTrue();
    expect(service.effectiveTenantId()).toBe(3);
    expect(await service.defaultTenantId()).toBe(3);
    service.enterTenantRoute(3);
    expect(service.effectiveTenantId()).toBe(3);
    expect(service.noAccess()).toBeFalse();
  });

  it('reports no access once the loaded list lacks the fixed tenant', async () => {
    const service = setup({ fixedTenantId: 9 });
    await service.whenLoaded();
    service.enterTenantRoute(9);

    expect(service.noAccess()).toBeTrue();
  });

  it('keeps last-used selection when no tenant is fixed', async () => {
    const service = setup({});

    expect(service.isFixed).toBeFalse();
    expect(service.fixedTenantId).toBeNull();
    expect(await service.defaultTenantId()).toBe(1);
  });
});
