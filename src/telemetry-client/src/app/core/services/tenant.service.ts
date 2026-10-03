import { Injectable, computed, inject, signal } from '@angular/core';
import { Tenant } from '../models/alert.models';
import { TenantsApiService } from './api/tenants-api.service';

const LAST_USED_KEY = 'selectedTenantId';

/**
 * The tenant a page is about. The route is the source of truth (`/t/:tenantId/...`, set by
 * `tenantRouteGuard` before the page's component exists), so a copied link carries its tenant.
 * localStorage keeps only "last used", which decides where a tenant-less URL redirects to.
 */
@Injectable({ providedIn: 'root' })
export class TenantService {
  private readonly api = inject(TenantsApiService);

  /** The tenants the caller may access (the server filters the list by its access check). */
  readonly tenants = signal<Tenant[]>([]);
  readonly tenantsLoaded = signal(false);
  /** The tenant in the current `/t/:tenantId` route, or null off a tenant route (e.g. settings). */
  readonly routeTenantId = signal<number | null>(null);
  /** A tenant the API answered 403 for. */
  readonly forbiddenTenantId = signal<number | null>(null);
  private readonly lastUsedId = signal<number | null>(this.loadLastUsed());

  /** The route's tenant if any, else the last used one that is still permitted, else the first permitted. */
  readonly effectiveTenantId = computed(() => {
    const route = this.routeTenantId();
    if (route !== null) return route;
    const tenants = this.tenants();
    const last = this.lastUsedId();
    if (last !== null && (!this.tenantsLoaded() || tenants.some((t) => t.id === last))) return last;
    return tenants[0]?.id ?? null;
  });

  readonly selectedTenant = computed<Tenant | null>(() => {
    const id = this.effectiveTenantId();
    if (id === null) return null;
    return this.tenants().find((t) => t.id === id) ?? { id, name: `Tenant ${id}` };
  });

  /** True when the route names a tenant the caller cannot use: refused by the API, or absent from the permitted list. */
  readonly noAccess = computed(() => {
    const id = this.routeTenantId();
    if (id === null) return false;
    return this.forbiddenTenantId() === id || (this.tenantsLoaded() && !this.tenants().some((t) => t.id === id));
  });

  private loaded?: Promise<Tenant[]>;

  loadTenants(): void {
    void this.whenLoaded();
  }

  /** Resolves with the tenant list once it has been fetched (an empty list if the fetch failed). */
  whenLoaded(): Promise<Tenant[]> {
    this.loaded ??= new Promise<Tenant[]>((resolve) => {
      this.api.getTenants().subscribe({
        next: (tenants) => {
          this.tenants.set(tenants);
          this.tenantsLoaded.set(true);
          resolve(tenants);
        },
        // Leave tenantsLoaded false: with no list we cannot say a route's tenant is not permitted.
        error: () => resolve([]),
      });
    });
    return this.loaded;
  }

  /** Where a tenant-less URL goes: last used if permitted, else the first permitted tenant, else null. */
  async defaultTenantId(): Promise<number | null> {
    const tenants = await this.whenLoaded();
    const last = this.lastUsedId();
    if (last !== null && (tenants.length === 0 || tenants.some((t) => t.id === last))) return last;
    return tenants[0]?.id ?? null;
  }

  /** Called by the route guard before a tenant page activates. */
  enterTenantRoute(id: number): void {
    this.routeTenantId.set(id);
    if (this.forbiddenTenantId() !== id) this.forbiddenTenantId.set(null);
    this.setLastUsed(id);
  }

  leaveTenantRoute(): void {
    this.routeTenantId.set(null);
  }

  markForbidden(id: number): void {
    this.forbiddenTenantId.set(id);
  }

  setLastUsed(id: number): void {
    this.lastUsedId.set(id);
    try { localStorage.setItem(LAST_USED_KEY, String(id)); } catch { /* unavailable */ }
  }

  /** The tenant API services build their URLs for. Throws off a tenant route: that is a bug in the caller. */
  requireTenantId(): number {
    const id = this.routeTenantId() ?? this.effectiveTenantId();
    if (id === null) throw new Error('No tenant is selected.');
    return id;
  }

  /** Router commands for a tenant page: `link('traces', id)` is `/t/{tenant}/traces/{id}`. */
  link(...segments: (string | number)[]): (string | number)[] {
    return ['/t', this.effectiveTenantId() ?? 0, ...segments];
  }

  private loadLastUsed(): number | null {
    try {
      const id = Number(localStorage.getItem(LAST_USED_KEY));
      return Number.isInteger(id) && id > 0 ? id : null;
    } catch {
      return null;
    }
  }
}
