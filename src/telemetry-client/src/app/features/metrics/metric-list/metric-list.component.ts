import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { FormsModule } from '@angular/forms';

import { MetricsApiService } from '../../../core/services/api/metrics-api.service';
import { TimeRangeService } from '../../../core/services/time-range.service';
import { MetricInfo, MetricType } from '../../../core/models/metric.models';
import { StatCardComponent } from '../../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';

interface UniqueMetric {
  name: string;
  type: MetricType;
  unit: string | null;
  description: string | null;
  instanceCount: number;
  services: string[];
  lastSeen: string;
}

const TYPE_LABELS: Record<MetricType, string> = {
  [MetricType.Gauge]: 'Gauge',
  [MetricType.Sum]: 'Counter',
  [MetricType.Histogram]: 'Histogram',
  [MetricType.ExponentialHistogram]: 'Exp. Histogram',
  [MetricType.Summary]: 'Summary',
};

@Component({
  selector: 'app-metric-list',
  standalone: true,
  imports: [
    DatePipe, FormsModule,
    MatCardModule, MatTableModule, MatIconModule, MatButtonToggleModule,
    MatSelectModule, MatFormFieldModule, MatInputModule,
    MatProgressBarModule, MatChipsModule,
    StatCardComponent, EmptyStateComponent,
  ],
  templateUrl: './metric-list.component.html',
  styleUrl: './metric-list.component.scss',
})
export class MetricListComponent implements OnInit {
  private readonly api = inject(MetricsApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly router = inject(Router);

  protected loading = signal(true);
  protected allMetrics = signal<MetricInfo[]>([]);
  protected searchText = signal('');
  protected selectedService = signal('');
  protected selectedType = signal<MetricType | -1>(-1);
  protected showUnique = signal(true);

  protected readonly typeLabels = TYPE_LABELS;
  protected readonly MetricType = MetricType;
  protected readonly metricTypes = Object.values(MetricType).filter((v) => typeof v === 'number') as MetricType[];

  protected services = computed(() => [
    ...new Set(this.allMetrics().map((m) => m.serviceName).filter(Boolean) as string[]),
  ]);

  protected uniqueMetrics = computed<UniqueMetric[]>(() => {
    const groups = new Map<string, MetricInfo[]>();
    for (const m of this.allMetrics()) {
      const list = groups.get(m.name) ?? [];
      list.push(m);
      groups.set(m.name, list);
    }
    return [...groups.entries()].map(([name, items]) => ({
      name,
      type: items[0].type,
      unit: items[0].unit,
      description: items[0].description,
      instanceCount: items.length,
      services: [...new Set(items.map((i) => i.serviceName).filter(Boolean) as string[])],
      lastSeen: items.reduce((a, b) => (a > b.lastSeen ? a : b.lastSeen), items[0].lastSeen),
    }));
  });

  protected filteredUnique = computed<UniqueMetric[]>(() => {
    const text = this.searchText().toLowerCase();
    const svc = this.selectedService();
    const type = this.selectedType();
    return this.uniqueMetrics().filter((m) => {
      if (text && !m.name.toLowerCase().includes(text)) return false;
      if (type !== -1 && m.type !== type) return false;
      if (svc && !m.services.includes(svc)) return false;
      return true;
    });
  });

  protected filteredAll = computed<MetricInfo[]>(() => {
    const text = this.searchText().toLowerCase();
    const svc = this.selectedService();
    const type = this.selectedType();
    return this.allMetrics().filter((m) => {
      if (text && !m.name.toLowerCase().includes(text)) return false;
      if (type !== -1 && m.type !== type) return false;
      if (svc && m.serviceName !== svc) return false;
      return true;
    });
  });

  protected gaugeCount = computed(() => this.allMetrics().filter((m) => m.type === MetricType.Gauge).length);
  protected counterCount = computed(() => this.allMetrics().filter((m) => m.type === MetricType.Sum).length);
  protected histogramCount = computed(() => this.allMetrics().filter((m) => m.type === MetricType.Histogram).length);

  protected readonly uniqueCols = ['name', 'type', 'unit', 'instances', 'services', 'lastSeen'];
  protected readonly allCols = ['name', 'type', 'unit', 'service', 'lastSeen'];
  protected readonly typeLabel = (t: MetricType) => TYPE_LABELS[t] ?? 'Unknown';

  ngOnInit(): void {
    const { start, end } = this.timeRange.range();
    this.api.getAllMetrics(start, end, 500).subscribe({
      next: (metrics) => { this.allMetrics.set(metrics); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  protected navigate(name: string): void {
    this.router.navigate(['/metrics', encodeURIComponent(name)]);
  }
}
