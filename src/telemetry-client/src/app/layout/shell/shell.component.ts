import { BreakpointObserver } from '@angular/cdk/layout';
import { AsyncPipe } from '@angular/common';
import { Component, inject, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatRippleModule } from '@angular/material/core';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatSidenav, MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map, startWith } from 'rxjs/operators';
import { APP_CONFIG } from '../../core/config/app-config';
import { ThemeService } from '../../core/services/theme.service';
import { TenantService } from '../../core/services/tenant.service';
import { TimeRangePickerComponent } from '../../shared/components/time-range-picker/time-range-picker.component';
import { Tenant } from '../../core/models/alert.models';
import { FormsModule } from '@angular/forms';

interface NavItem {
  label: string;
  icon: string;
  route: string;
}

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
  private readonly breakpoints = inject(BreakpointObserver);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /** Header bar text — configurable per host, see AppConfig.brandName/brandTagline. */
  protected readonly brand = inject(APP_CONFIG);

  /** True below COMPACT_QUERY: hamburger + overlay drawer instead of the permanent nav rail. */
  protected readonly isCompact$ = this.breakpoints
    .observe(COMPACT_QUERY)
    .pipe(map((r) => r.matches));

  /**
   * True on pages that opt out of the per-tenant chrome — the tenant picker, and every nav item
   * except Settings. Those are tenant-scoped controls: the picker chooses a tenant, and each
   * other nav item routes to a page that reads one. On the cross-tenant Global Dashboard none of
   * that has anything to act on, so the page declares `data: { chrome: 'global' }` and the shell
   * drops the picker and narrows the rail to Settings, keeping the toolbar (branding, time range,
   * theme) and Settings itself, which isn't tenant-scoped.
   *
   * Driven by route data rather than a URL test so a second chromeless page costs one line.
   */
  protected readonly chromeless = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map(() => this.isChromeless()),
      startWith(this.isChromeless()),
    ),
    { initialValue: false },
  );

  /**
   * `route` is the shell's own (path-less) route, so its snapshot's `firstChild` is the page
   * being rendered. Read off the snapshot rather than `route.firstChild?.snapshot`: during the
   * component's field initialization the child ActivatedRoute exists before its snapshot does,
   * so that form throws on the initial `startWith`.
   */
  private isChromeless(): boolean {
    return this.route.snapshot.firstChild?.data?.['chrome'] === 'global';
  }

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
    this.tenantService.selectTenant(tenant);
  }

  protected compareTenants(a: Tenant | null, b: Tenant | null): boolean {
    return a?.id === b?.id;
  }
}
