import { Component, OnInit, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ThemeService } from './core/services/theme.service';
import { TenantService } from './core/services/tenant.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: `<router-outlet />`,
})
export class App implements OnInit {
  private readonly themeService = inject(ThemeService);
  private readonly tenantService = inject(TenantService);

  ngOnInit(): void {
    this.themeService.setMode(this.themeService.mode());
    this.tenantService.loadTenants();
  }
}
