import { Routes } from '@angular/router';
import { ShellComponent } from './layout/shell/shell.component';
import {
  legacyTenantRedirect, tenantRouteGuard, tenantRouteLeaveGuard,
} from './core/guards/tenant-route.guards';

/**
 * Tenant-scoped pages live under `t/:tenantId/`, so the URL (and a copied link) names its tenant.
 * `settings` is global. The tenant-less paths of earlier versions redirect under the last used
 * tenant (`legacyTenantRedirect`), keeping bookmarks and their query strings working.
 */
const legacy = (path: string) => ({ path, pathMatch: 'full' as const, canActivate: [legacyTenantRedirect], children: [] });

export const routes: Routes = [
  {
    path: 'callback',
    loadComponent: () => import('./core/auth/callback.component').then((m) => m.AuthCallbackComponent),
  },
  {
    path: '',
    component: ShellComponent,
    children: [
      legacy(''),
      {
        path: 't/:tenantId',
        canActivate: [tenantRouteGuard],
        canDeactivate: [tenantRouteLeaveGuard],
        runGuardsAndResolvers: 'paramsChange',
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
          {
            path: 'dashboard',
            title: 'Dashboard',
            loadComponent: () =>
              import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
          },
          {
            path: 'traces',
            title: 'Traces',
            loadComponent: () =>
              import('./features/traces/trace-list/trace-list.component').then((m) => m.TraceListComponent),
          },
          {
            path: 'traces/:id',
            loadComponent: () =>
              import('./features/traces/trace-detail/trace-detail.component').then(
                (m) => m.TraceDetailComponent
              ),
          },
          {
            path: 'metrics',
            title: 'Metrics',
            loadComponent: () =>
              import('./features/metrics/metric-list/metric-list.component').then(
                (m) => m.MetricListComponent
              ),
          },
          {
            path: 'metrics/:name',
            loadComponent: () =>
              import('./features/metrics/metric-detail/metric-detail.component').then(
                (m) => m.MetricDetailComponent
              ),
          },
          {
            path: 'logs',
            title: 'Logs',
            loadComponent: () =>
              import('./features/logs/logs.component').then((m) => m.LogsComponent),
          },
          {
            path: 'alerts',
            title: 'Alerts',
            loadComponent: () =>
              import('./features/alerts/alerts.component').then((m) => m.AlertsComponent),
          },
        ],
      },
      {
        path: 'settings',
        title: 'Settings',
        loadComponent: () =>
          import('./features/settings/settings.component').then((m) => m.SettingsComponent),
      },
      {
        path: 'no-tenants',
        title: 'No tenants',
        loadComponent: () =>
          import('./features/no-tenants/no-tenants.component').then((m) => m.NoTenantsComponent),
      },
      legacy('dashboard'),
      legacy('traces'),
      legacy('traces/:id'),
      legacy('metrics'),
      legacy('metrics/:name'),
      legacy('logs'),
      legacy('alerts'),
    ],
  },
];
