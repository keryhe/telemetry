import { Component, EventEmitter, Input, Output, computed, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { MatSortModule, Sort } from '@angular/material/sort';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ServiceStats } from '../../../core/models/trace.models';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { formatDuration } from '../../../shared/utils/chart.utils';

type SortDir = '' | 'asc' | 'desc';

/**
 * Per-service RED metrics table for the dashboard (Phase 5 of the dashboard refactor). Purely
 * presentational + client-side sort — `services` comes from the dashboard's single `/overview`
 * fetch (see traces-api.service.ts), so this component makes no requests of its own. Clicking a
 * row toggles that service as the dashboard's active filter; the toggle semantics (select vs.
 * clear) live in the parent, which already owns `selectedService` — this component just reports
 * which row was clicked and which one is currently active.
 */
@Component({
  selector: 'app-service-health-table',
  standalone: true,
  imports: [DecimalPipe, MatTableModule, MatSortModule, MatTooltipModule, EmptyStateComponent],
  templateUrl: './service-health-table.component.html',
  styleUrl: './service-health-table.component.scss',
})
export class ServiceHealthTableComponent {
  @Input({ required: true }) set services(value: ServiceStats[]) {
    this._services.set(value);
  }
  @Input() activeService = '';
  @Output() rowClick = new EventEmitter<string>();

  private _services = signal<ServiceStats[]>([]);
  protected sortState = signal<{ col: string; dir: SortDir }>({ col: 'count', dir: 'desc' });

  protected rows = computed<ServiceStats[]>(() => {
    const rows = [...this._services()];
    const { col, dir } = this.sortState();
    if (!dir) return rows;
    const sign = dir === 'asc' ? 1 : -1;
    const key = (r: ServiceStats): number | string => {
      switch (col) {
        case 'service': return r.service;
        case 'rate': return r.ratePerSecond;
        case 'errorRate': return r.errorRate;
        case 'avg': return r.avgMs;
        default: return r.count;
      }
    };
    return rows.sort((a, b) => {
      const ka = key(a), kb = key(b);
      return typeof ka === 'string' || typeof kb === 'string'
        ? sign * String(ka).localeCompare(String(kb))
        : sign * ((ka as number) - (kb as number));
    });
  });

  protected readonly columns = ['service', 'count', 'rate', 'errorRate', 'avg'];
  protected readonly formatDuration = formatDuration;

  protected onSort(s: Sort): void {
    this.sortState.set({ col: s.active, dir: (s.direction || 'desc') as SortDir });
  }
}
