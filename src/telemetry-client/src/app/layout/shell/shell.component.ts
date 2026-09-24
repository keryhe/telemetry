import { BreakpointObserver } from '@angular/cdk/layout';
import { AsyncPipe } from '@angular/common';
import { Component, inject, signal, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatRippleModule } from '@angular/material/core';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatSidenav, MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs/operators';
import { APP_CONFIG } from '../../core/config/app-config';
import { ThemeService } from '../../core/services/theme.service';
import { TenantService } from '../../core/services/tenant.service';
import { TimeRangePickerComponent } from '../../shared/components/time-range-picker/time-range-picker.component';
import { Tenant } from '../../core/models/alert.models';
import { resetTenantScopedPageState } from '../../shared/utils/page-state';
import { FormsModule } from '@angular/forms';

interface NavItem {
  label: string;
  icon: string;
  route: string;
}

/** Query params that name something belonging to one tenant, dropped on a tenant switch. */
const TENANT_SCOPED_QUERY_PARAMS = ['service', 'op', 'q', 'severity', 'page', 'span'];
/** The time-range params — the only ones carried from a detail page back to its list. */
const TIME_RANGE_QUERY_PARAMS = ['range', 'from', 'to'];
/** Detail routes whose item belongs to one tenant, mapped to the list page to fall back to. */
const DETAIL_ROUTE_PARENTS: Record<string, string> = { traces: '/traces', metrics: '/metrics' };

/**
 * Width at which the shell switches to its compact layout (drawer instead of a permanent rail).
 *
 * Deliberately width-only, and deliberately NOT Angular CDK's `Breakpoints.Handset`: that constant
 * is two queries — `(max-width: 599.98px) and (orientation: portrait)` OR
 * `(max-width: 959.98px) and (orientation: landscape)`. In a browser "orientation" is just
 * width-vs-height, so dragging a desktop window narrower flips it mid-drag and the sidenav
 * collapsed at 960, reappeared once width dropped below the window's height, then collapsed again
 * at 600. A single width query gives exactly one transition, wherever the window's height happens
 * to be.
 *
 * The same 599.98px threshold is hardcoded in two CSS media queries that compact the rest of the
 * shell at the same point — the toolbar block in shell.component.scss and the icon-only trigger in
 * shared/components/time-range-picker/time-range-picker.component.ts. A TS constant can't be shared
 * into a CSS media query, so if this value changes, change those two as well.
 */
const COMPACT_QUERY = '(max-width: 599.98px)';

// A second, wider threshold — 959.98px, the upper bound of Angular CDK's `Breakpoints.Small`,
// same "borrow a CDK constant" reasoning as above — lives only as a media query, in two places:
// shell.component.scss (`.toolbar-controls`) wraps the tenant select / time-range picker / theme
// toggle off the title row onto a second line, which COMPACT_QUERY then splits into a third; and
// styles.scss hands page scrolling back to the routed host so the page header scrolls away
// instead of staying pinned. No TS-driven behavior depends on it, so it isn't declared as a
// constant here, but it's documented next to COMPACT_QUERY since the thresholds work together.

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [
    AsyncPipe, FormsModule,
    RouterOutlet, RouterLink, RouterLinkActive,
    MatSidenavModule, MatToolbarModule, MatRippleModule,
    MatIconModule, MatButtonModule, MatTooltipModule,
    MatSelectModule, MatFormFieldModule,
    TimeRangePickerComponent,
  ],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
})
export class ShellComponent {
  // Not otherwise referenced here now that the theme controls live on the settings page — kept
  // so the shell (always instantiated, unlike a lazy-loaded route) forces this singleton's
  // constructor to run at startup and apply the persisted theme class to <body> immediately.
  private readonly themeService = inject(ThemeService);
  protected readonly tenantService = inject(TenantService);
  private readonly router = inject(Router);

  /**
   * Keys the `<router-outlet>`: bumping it destroys and recreates the outlet, which re-activates the
   * current route with a freshly constructed page component. That is how a tenant switch reloads
   * the page -- no feature component reads the selected tenant, so every one of them would
   * otherwise keep showing the previous tenant's data (including its once-fetched services list).
   */
  protected readonly outletKey = signal(0);
  private readonly breakpoints = inject(BreakpointObserver);

  /** Header bar text — configurable per host, see AppConfig.brandName/brandTagline. */
  protected readonly brand = inject(APP_CONFIG);

  /** True below COMPACT_QUERY: hamburger + overlay drawer instead of the permanent nav rail. */
  protected readonly isCompact$ = this.breakpoints
    .observe(COMPACT_QUERY)
    .pipe(map((r) => r.matches));

  /**
   * Queried rather than referenced as `#drawer` in the template: the sidenav now lives inside an
   * `@if`, which scopes a template reference variable to that block, and the toolbar's hamburger
   * sits outside it.
   */
  private readonly drawer = viewChild(MatSidenav);

  protected toggleDrawer(): void {
    void this.drawer()?.toggle();
  }

  protected readonly navItems: NavItem[] = [
    { label: 'Dashboard', icon: 'dashboard', route: '/dashboard' },
    { label: 'Logs', icon: 'article', route: '/logs' },
    { label: 'Traces', icon: 'account_tree', route: '/traces' },
    { label: 'Metrics', icon: 'bar_chart', route: '/metrics' },
    { label: 'Alerts', icon: 'notifications', route: '/alerts' },
    { label: 'Settings', icon: 'settings', route: '/settings' },
  ];

  protected selectTenant(tenant: Tenant): void {
    if (tenant.id === this.tenantService.selectedTenant()?.id) return;
    this.tenantService.selectTenant(tenant);
    resetTenantScopedPageState();

    // Drop the old tenant's filters from the URL, and leave a detail page (its trace/metric belongs
    // to the old tenant) for its list. Only then recreate the page, so the new component reads the
    // cleaned URL rather than the stale one.
    const tree = this.router.parseUrl(this.router.url);
    const segments = tree.root.children['primary']?.segments.map((s) => s.path) ?? [];
    const parent = segments.length > 1 ? DETAIL_ROUTE_PARENTS[segments[0]] : undefined;
    let target;
    if (parent) {
      const params = Object.fromEntries(
        Object.entries(tree.queryParams).filter(([k]) => TIME_RANGE_QUERY_PARAMS.includes(k)));
      target = this.router.createUrlTree([parent], { queryParams: params });
    } else {
      for (const key of TENANT_SCOPED_QUERY_PARAMS) delete tree.queryParams[key];
      target = tree;
    }
    // Leaving for the list page already constructs a fresh component; staying on the same route
    // reuses the current one, so recreate the outlet to rebuild it.
    this.router.navigateByUrl(target, { replaceUrl: true })
      .finally(() => { if (!parent) this.outletKey.update((k) => k + 1); });
  }

  protected compareTenants(a: Tenant | null, b: Tenant | null): boolean {
    return a?.id === b?.id;
  }
}
