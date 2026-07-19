import { BreakpointObserver, Breakpoints } from '@angular/cdk/layout';
import { AsyncPipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatRippleModule } from '@angular/material/core';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatMenuModule } from '@angular/material/menu';
import { MatSelectModule } from '@angular/material/select';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs/operators';
import { ThemeMode, ThemeService } from '../../core/services/theme.service';
import { TenantService } from '../../core/services/tenant.service';
import { TimeRangePickerComponent } from '../../shared/components/time-range-picker/time-range-picker.component';
import { Tenant } from '../../core/models/alert.models';
import { FormsModule } from '@angular/forms';

interface NavItem {
  label: string;
  icon: string;
  route: string;
}

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [
    AsyncPipe, FormsModule,
    RouterOutlet, RouterLink, RouterLinkActive,
    MatSidenavModule, MatToolbarModule, MatRippleModule,
    MatIconModule, MatButtonModule, MatTooltipModule,
    MatMenuModule, MatSelectModule, MatFormFieldModule,
    TimeRangePickerComponent,
  ],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
})
export class ShellComponent {
  protected readonly themeService = inject(ThemeService);
  protected readonly tenantService = inject(TenantService);
  private readonly breakpoints = inject(BreakpointObserver);

  protected readonly isHandset$ = this.breakpoints
    .observe(Breakpoints.Handset)
    .pipe(map((r) => r.matches));

  protected readonly navItems: NavItem[] = [
    { label: 'Dashboard', icon: 'dashboard', route: '/dashboard' },
    { label: 'Logs', icon: 'article', route: '/logs' },
    { label: 'Traces', icon: 'account_tree', route: '/traces' },
    { label: 'Metrics', icon: 'bar_chart', route: '/metrics' },
    { label: 'Alerts', icon: 'notifications', route: '/alerts' },
  ];

  protected readonly themeModes: { value: ThemeMode; icon: string; label: string }[] = [
    { value: 'light', icon: 'light_mode', label: 'Light' },
    { value: 'dark', icon: 'dark_mode', label: 'Dark' },
    { value: 'system', icon: 'contrast', label: 'System' },
  ];

  protected get themeIcon(): string {
    return this.themeModes.find((m) => m.value === this.themeService.mode())?.icon ?? 'contrast';
  }

  protected setTheme(mode: ThemeMode): void {
    this.themeService.setMode(mode);
  }

  protected selectTenant(tenant: Tenant): void {
    this.tenantService.selectTenant(tenant);
  }

  protected compareTenants(a: Tenant | null, b: Tenant | null): boolean {
    return a?.id === b?.id;
  }
}
