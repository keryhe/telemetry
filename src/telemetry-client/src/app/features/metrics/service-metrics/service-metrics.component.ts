import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { concatMap, from, tap, toArray } from 'rxjs';
import { MatCardModule } from '@angular/material/card';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { NgApexchartsModule } from 'ng-apexcharts';
import type { ApexOptions } from 'ng-apexcharts';

import { MetricsApiService } from '../../../core/services/api/metrics-api.service';
import { TracesApiService } from '../../../core/services/api/traces-api.service';
import { TimeRangeService } from '../../../core/services/time-range.service';
import { ThemeService } from '../../../core/services/theme.service';
import { MetricType } from '../../../core/models/metric.models';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { computeRateSeries, isCounterMetric } from '../../../shared/utils/chart.utils';

interface SparkCard {
  name: string;
  type: MetricType;
  unit: string | null;
  current: number | null;
  min: number | null;
  max: number | null;
  chartOptions: ApexOptions;
}

const TYPE_LABELS: Record<MetricType, string> = {
  [MetricType.Gauge]: 'Gauge', [MetricType.Sum]: 'Counter',
  [MetricType.Histogram]: 'Histogram', [MetricType.ExponentialHistogram]: 'Exp. Histogram',
  [MetricType.Summary]: 'Summary',
};

@Component({
  selector: 'app-service-metrics',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    MatCardModule, MatExpansionModule, MatFormFieldModule, MatIconModule,
    MatProgressBarModule, MatSelectModule, NgApexchartsModule,
    EmptyStateComponent,
  ],
  templateUrl: './service-metrics.component.html',
  styleUrl: './service-metrics.component.scss',
})
export class ServiceMetricsComponent {
  private readonly api = inject(MetricsApiService);
  private readonly tracesApi = inject(TracesApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly theme = inject(ThemeService);

  protected loading = signal(true);
  protected loadingProgress = signal(0);
  protected loadingTotal = signal(0);
  protected services = signal<string[]>([]);
  protected selectedService = signal(localStorage.getItem('serviceMetrics_service') ?? '');
  protected sparkCards = signal<SparkCard[]>([]);

  protected gaugeCards = computed(() => this.sparkCards().filter((c) => c.type === MetricType.Gauge));
  protected counterCards = computed(() => this.sparkCards().filter((c) => c.type === MetricType.Sum));
  protected otherCards = computed(() => this.sparkCards().filter(
    (c) => c.type !== MetricType.Gauge && c.type !== MetricType.Sum
  ));

  readonly MetricType = MetricType;
  readonly typeLabels = TYPE_LABELS;

  constructor() {
    effect(() => {
      this.timeRange.range();
      untracked(() => this.load());
    });
  }

  private load(): void {
    const { start, end } = this.timeRange.range();
    this.tracesApi.getServices(start, end).subscribe((svcs) => {
      this.services.set(svcs);
      const svc = this.selectedService() || svcs[0] || '';
      this.selectedService.set(svc);
      if (svc) this.loadMetrics(svc);
    });
  }

  protected onServiceChange(svc: string): void {
    this.selectedService.set(svc);
    localStorage.setItem('serviceMetrics_service', svc);
    this.loadMetrics(svc);
  }

  private loadMetrics(serviceName: string): void {
    this.loading.set(true);
    this.loadingProgress.set(0);
    this.sparkCards.set([]);
    const { start, end } = this.timeRange.range();

    this.api.getByService(serviceName, start, end).subscribe((metrics) => {
      const names = [...new Set(metrics.map((m) => m.name))];
      this.loadingTotal.set(names.length);

      from(names).pipe(
        concatMap((name) =>
          this.api.getSeries({
            metricName: name, start, end,
            metricId: metrics.find((m) => m.name === name)?.id,
          })
        ),
        tap(() => this.loadingProgress.update((n) => n + 1)),
        toArray(),
      ).subscribe({
        next: (seriesArray) => {
          const cards: SparkCard[] = seriesArray.map((s) => {
            const metricInfo = metrics.find((m) => m.name === s.name);
            const type = metricInfo?.type ?? MetricType.Gauge;
            const isCounter = isCounterMetric(type, s.points);

            // Counters render as per-second rate (no raw toggle on this page).
            const data: [number, number][] = isCounter
              ? computeRateSeries(s.points)
              : s.points.map((p) => [new Date(p.timestamp).getTime(), p.doubleValue ?? p.intValue ?? 0]);
            const vals = data.map(([, v]) => v);
            const unit = isCounter ? `${metricInfo?.unit ?? ''}/s`.replace(/^\/s$/, 'rate/s') : (metricInfo?.unit ?? null);

            const isDark = this.theme.isDark();
            return {
              name: s.name,
              type,
              unit,
              current: vals.length ? vals[vals.length - 1] : null,
              min: vals.length ? Math.min(...vals) : null,
              max: vals.length ? Math.max(...vals) : null,
              chartOptions: {
                chart: { type: 'area', height: 60, sparkline: { enabled: true }, background: 'transparent' },
                theme: { mode: isDark ? 'dark' : 'light' },
                series: [{ name: s.name, data }],
                xaxis: { type: 'datetime' },
                stroke: { curve: 'smooth', width: 1.5 },
                fill: { opacity: 0.2 },
                tooltip: { x: { format: 'HH:mm' } },
              } as ApexOptions,
            };
          });
          this.sparkCards.set(cards);
          this.loading.set(false);
        },
        error: () => this.loading.set(false),
      });
    });
  }

  protected fmt(v: number | null): string {
    if (v === null) return '—';
    return Math.abs(v) >= 1000 ? (v / 1000).toFixed(1) + 'k' : v.toFixed(2);
  }
}
