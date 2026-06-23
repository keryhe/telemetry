import { Injectable, computed, inject, signal } from '@angular/core';
import { Tenant } from '../models/alert.models';
import { TenantsApiService } from './api/tenants-api.service';

@Injectable({ providedIn: 'root' })
export class TenantService {
  private readonly api = inject(TenantsApiService);

  readonly tenants = signal<Tenant[]>([]);
  readonly selectedTenant = signal<Tenant | null>(this.loadStoredTenant());
  readonly hasTenant = computed(() => this.selectedTenant() !== null);

  loadTenants(): void {
    this.api.getTenants().subscribe((tenants) => {
      this.tenants.set(tenants);
      if (!this.selectedTenant() && tenants.length > 0) {
        this.selectTenant(tenants[0]);
      } else if (this.selectedTenant()) {
        // Refresh stored tenant from the fresh list (name may have changed)
        const refreshed = tenants.find((t) => t.id === this.selectedTenant()!.id);
        if (refreshed) this.selectedTenant.set(refreshed);
      }
    });
  }

  selectTenant(tenant: Tenant): void {
    this.selectedTenant.set(tenant);
    localStorage.setItem('selectedTenantId', String(tenant.id));
    localStorage.setItem('selectedTenantName', tenant.name);
  }

  private loadStoredTenant(): Tenant | null {
    const id = localStorage.getItem('selectedTenantId');
    const name = localStorage.getItem('selectedTenantName');
    if (id && name) return { id: Number(id), name };
    return null;
  }
}
