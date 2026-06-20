import { Component, Input, OnInit, computed, inject, signal } from '@angular/core';
import { SlicePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { NgApexchartsModule } from 'ng-apexcharts';
import type { ApexOptions } from 'ng-apexcharts';

import { MetricsApiService } from '../../../core/services/api/metrics-api.service';
import { TimeRangeService } from '../../../core/services/time-range.service';
import { ThemeService } from '../../../core/services/theme.service';
import {
  MetricDataPoint, MetricInfo, MetricSeries, MetricType, MultiSeriesMetricData,
} from '../../../core/models/metric.models';
import { StatCardComponent } from '../../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';

const TYPE_LABELS: Record<MetricType, string> = {
  [MetricType.Gauge]: 'Gauge', [MetricType.Sum]: 'Counter',
  [MetricType.Histogram]: 'Histogram', [MetricType.ExponentialHistogram]: 'Exp. Histogram',
  [MetricType.Summary]: 'Summary',
};

@Component({
  selector: 'app-metric-detail',
  standalone: true,
  imports: [
    SlicePipe, RouterLink,
    MatCardModule, MatButtonModule, MatButtonToggleModule, MatIconModule,
    MatTabsModule, MatTableModule, MatChipsModule, MatFormFieldModule,
    MatSelectModule, MatProgressBarModule, NgApexchartsModule,
    StatCardComponent, EmptyStateComponent,
  ],
  templateUrl: './metric-detail.component.html',
  styleUrl: './metric-detail.component.scss',
})
export class MetricDetailComponent implements OnInit {
  @Input() name!: string;

  private readonly api = inject(MetricsApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly theme = inject(ThemeService);

  protected loading = signal(true);
  protected metricName = computed(() => decodeURIComponent(this.name));
  protected instances = signal<MetricInfo[]>([]);
  protected labels = signal<Record<string, string[]>>({});
  protected series = signal<MetricSeries | null>(null);
  protected multiSeries = signal<MultiSeriesMetricData | null>(null);

  protected selectedService = signal('');
  protected selectedLabels = signal<Record<string, string>>({});
  protected showPerService = signal(false);
  protected showRaw = signal(false);

  protected metricType = computed(() => this.instances()[0]?.type ?? MetricType.Gauge);
  protected isCounter = computed(() => this.metricType() === MetricType.Sum);
  protected isHistogram = computed(() => this.metricType() === MetricType.Histogram);
  protected typeLabel = computed(() => TYPE_LABELS[this.metricType()] ?? 'Unknown');

  protected services = computed(() =>
    [...new Set(this.instances().map((i) => i.serviceName).filter(Boolean) as string[])]
  );
  protected labelKeys = computed(() => Object.keys(this.labels()));

  protected chartOptions = signal<ApexOptions>({});

  protected currentValue = computed(() => this.lastPoint()?.doubleValue ?? this.lastPoint()?.intValue ?? null);
  protected minValue = computed(() => this.computeMin());
  protected maxValue = computed(() => this.computeMax());
  protected avgValue = computed(() => this.computeAvg());

  protected exemplars = computed(() =>
    (this.series()?.points ?? [])
      .flatMap((p) => p.exemplars ?? [])
      .filter((e) => e.traceIdHex)
  );

  readonly typeLabel2 = (t: MetricType) => TYPE_LABELS[t];

  ngOnInit(): void {
    this.loadAll();
  }

  private loadAll(): void {
    this.loading.set(true);
    const { start, end } = this.timeRange.range();
    const name = this.metricName();

    forkJoin({
      instances: this.api.getByName(name),
      labels: this.api.getLabels(name),
      series: this.api.getSeries({ metricName: name, start, end }),
    }).subscribe({
      next: ({ instances, labels, series }) => {
        this.instances.set(instances);
        this.labels.set(labels);
        this.series.set(series);
        this.buildChart();
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected reloadSeries(): void {
    const { start, end } = this.timeRange.range();
    const name = this.metricName();
    const svc = this.selectedService();
    const metricId = svc
      ? this.instances().find((i) => i.serviceName === svc)?.id
      : undefined;

    if (this.showPerService()) {
      this.api.getSeriesByService(name, start, end).subscribe((multi) => {
        this.multiSeries.set(multi);
        this.buildMultiChart(multi);
      });
      return;
    }

    this.api.getSeries({
      metricName: name, start, end,
      metricId,
      labelFilters: Object.keys(this.selectedLabels()).length > 0 ? this.selectedLabels() : undefined,
    }).subscribe((s) => {
      this.series.set(s);
      this.buildChart();
    });
  }

  private buildChart(): void {
    const s = this.series();
    if (!s) return;
    const isDark = this.theme.isDark();
    const points = s.points;
    const isCounter = this.isCounter();
    const showRaw = this.showRaw();

    let data: [number, number][];
    if (isCounter && !showRaw && points.length > 1) {
      data = points.slice(1).map((p, i) => {
        const prev = points[i];
        const dt = (new Date(p.timestamp).getTime() - new Date(prev.timestamp).getTime()) / 1000;
        const dv = ((p.doubleValue ?? p.intValue ?? 0) - (prev.doubleValue ?? prev.intValue ?? 0));
        return [new Date(p.timestamp).getTime(), dt > 0 ? dv / dt : 0];
      });
    } else {
      data = points.map((p) => [
        new Date(p.timestamp).getTime(),
        p.doubleValue ?? p.intValue ?? p.sum ?? 0,
      ]);
    }

    this.chartOptions.set({
      chart: { type: this.isHistogram() ? 'bar' : 'area', height: 300, toolbar: { show: false }, background: 'transparent' },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: [{ name: s.name, data }],
      xaxis: { type: 'datetime' },
      stroke: { curve: 'smooth', width: 2 },
      fill: { opacity: 0.15 },
      dataLabels: { enabled: false },
      yaxis: { labels: { formatter: (v: number) => v.toFixed(2) } },
    });
  }

  private buildMultiChart(multi: MultiSeriesMetricData): void {
    const isDark = this.theme.isDark();
    this.chartOptions.set({
      chart: { type: 'line', height: 300, toolbar: { show: false }, background: 'transparent' },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: multi.series.map((s) => ({
        name: s.seriesName,
        data: s.points.map((p) => [new Date(p.timestamp).getTime(), p.doubleValue ?? p.intValue ?? 0] as [number, number]),
      })),
      xaxis: { type: 'datetime' },
      stroke: { curve: 'smooth', width: 2 },
      dataLabels: { enabled: false },
      legend: { position: 'top' },
    });
  }

  private lastPoint(): MetricDataPoint | null {
    const pts = this.series()?.points;
    return pts?.length ? pts[pts.length - 1] : null;
  }

  private computeMin(): number | null {
    const vals = this.series()?.points.map((p) => p.doubleValue ?? p.intValue ?? p.min ?? null).filter((v) => v !== null) as number[];
    return vals?.length ? Math.min(...vals) : null;
  }

  private computeMax(): number | null {
    const vals = this.series()?.points.map((p) => p.doubleValue ?? p.intValue ?? p.max ?? null).filter((v) => v !== null) as number[];
    return vals?.length ? Math.max(...vals) : null;
  }

  private computeAvg(): number | null {
    const vals = this.series()?.points.map((p) => p.doubleValue ?? p.intValue ?? null).filter((v) => v !== null) as number[];
    return vals?.length ? vals.reduce((a, b) => a + b, 0) / vals.length : null;
  }

  protected fmt(v: number | null): string {
    return v !== null ? v.toFixed(3) : '—';
  }

  protected updateLabelFilter(key: string, value: string): void {
    this.selectedLabels.update((l) => ({ ...l, [key]: value }));
    this.reloadSeries();
  }
}
