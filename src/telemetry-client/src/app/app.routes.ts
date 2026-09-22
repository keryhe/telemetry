import { Routes } from '@angular/router';
import { ShellComponent } from './layout/shell/shell.component';

export const routes: Routes = [
  { path: '', redirectTo: 'global', pathMatch: 'full' },
  {
    path: '',
    component: ShellComponent,
    children: [
      {
        // A child of the shell, not a sibling: the Global Dashboard keeps the toolbar (branding,
        // time-range picker, theme toggle) and the page scroll contract.
        path: 'global',
        title: 'Global',
        loadComponent: () =>
          import('./features/global-dashboard/global-dashboard.component').then(
            (m) => m.GlobalDashboardComponent
          ),
      },
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
      {
        path: 'settings',
        title: 'Settings',
        loadComponent: () =>
          import('./features/settings/settings.component').then((m) => m.SettingsComponent),
      },
    ],
  },
];
