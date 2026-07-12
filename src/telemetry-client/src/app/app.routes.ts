import { Routes } from '@angular/router';
import { ShellComponent } from './layout/shell/shell.component';

export const routes: Routes = [
  { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
  {
    path: '',
    component: ShellComponent,
    children: [
      {
        path: 'dashboard',
        title: 'Watchdog - Dashboard',
        loadComponent: () =>
          import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
      },
      {
        path: 'traces',
        title: 'Watchdog - Traces',
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
        title: 'Watchdog - Metrics',
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
        title: 'Watchdog - Logs',
        loadComponent: () =>
          import('./features/logs/logs.component').then((m) => m.LogsComponent),
      },
      {
        path: 'alerts',
        title: 'Watchdog - Alerts',
        loadComponent: () =>
          import('./features/alerts/alerts.component').then((m) => m.AlertsComponent),
      },
    ],
  },
];
