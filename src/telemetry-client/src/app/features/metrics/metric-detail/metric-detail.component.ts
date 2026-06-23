import { Component, Input, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe, DecimalPipe, KeyValuePipe, SlicePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Title } from '@angular/platform-browser';
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
  AggregationTemporality, MetricDataPoint, MetricInfo, MetricSeries, MetricType, MultiSeriesMetricData,
} from '../../../core/models/metric.models';
import { StatCardComponent } from '../../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { computeRateSeries, isCounterMetric, isDeltaSum } from '../../../shared/utils/chart.utils';
import { loadPageState, savePageState } from '../../../shared/utils/page-state';

const STATE_KEY = 'state.metricDetail';

const TYPE_LABELS: Record<MetricType, string> = {
  [MetricType.Gauge]: 'Gauge', [MetricType.Sum]: 'Counter',
  [MetricType.Histogram]: 'Histogram', [MetricType.ExponentialHistogram]: 'Exp. Histogram',
  [MetricType.Summary]: 'Summary',
};

const TEMPORALITY_LABELS: Record<AggregationTemporality, string> = {
  [AggregationTemporality.Unspecified]: 'Unspecified',
  [AggregationTemporality.Delta]: 'Delta',
  [AggregationTemporality.Cumulative]: 'Cumulative',
};

const val = (p: MetricDataPoint): number => p.doubleValue ?? p.intValue ?? 0;

@Component({
  selector: 'app-metric-detail',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, KeyValuePipe, SlicePipe, RouterLink,
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
  private readonly title = inject(Title);

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
  protected activeTab = signal(0);

  protected metricType = computed(() => this.instances()[0]?.type ?? MetricType.Gauge);
  protected points = computed(() => this.series()?.points ?? []);
  protected isCounter = computed(() => isCounterMetric(this.metricType(), this.points()));
  protected isDelta = computed(() => isDeltaSum(this.metricType(), this.points()));
  protected isHistogram = computed(() => this.metricType() === MetricType.Histogram);
  protected isExpHistogram = computed(() => this.metricType() === MetricType.ExponentialHistogram);
  protected isSummary = computed(() => this.metricType() === MetricType.Summary);
  protected typeLabel = computed(() => TYPE_LABELS[this.metricType()] ?? 'Unknown');

  /** Stats are rates only for a cumulative counter when not showing raw values. */
  protected statsAreRates = computed(() => this.isCounter() && !this.showRaw());
  protected statsUnitSuffix = computed(() => (this.statsAreRates() ? '/s' : ''));

  protected services = computed(() =>
    [...new Set(this.instances().map((i) => i.serviceName).filter(Boolean) as string[])]
  );
  protected labelKeys = computed(() => Object.keys(this.labels()));

  protected chartOptions = signal<ApexOptions>({});
  protected minMaxChartOptions = signal<ApexOptions | null>(null);

  /** Latest-point quantile snapshot for summary metrics. */
  protected summarySnapshot = computed(() => {
    const pts = this.points();
    if (!pts.length) return [];
    const latest = [...pts].sort((a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime())[0];
    const qs = latest.quantiles, vs = latest.quantileValues;
    if (!qs || !vs) return [];
    return qs.map((q, i) => ({ label: `P${(q * 100).toFixed(0)}`, value: vs[i] }))
      .filter((r) => r.value != null);
  });

  /** Stats: rate-based for counters, latest-point mean/min/max/count for histograms, raw otherwise. */
  private stats = computed(() => {
    const pts = this.points();
    if (!pts.length) return { current: null, min: null, max: null, avg: null };

    if (this.isHistogram()) {
      const latest = [...pts]
        .sort((a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime())
        .find((p) => (p.count ?? 0) > 0);
      if (!latest) return { current: null, min: null, max: null, avg: null };
      return {
        current: latest.sum != null && (latest.count ?? 0) > 0 ? latest.sum / latest.count! : null,
        min: latest.min,
        max: latest.max,
        avg: latest.count ?? null,
      };
    }

    if (this.statsAreRates()) {
      const rates = computeRateSeries(pts).map(([, r]) => r);
      if (!rates.length) return { current: null, min: null, max: null, avg: null };
      return {
        current: rates[rates.length - 1],
        min: Math.min(...rates),
        max: Math.max(...rates),
        avg: rates.reduce((a, b) => a + b, 0) / rates.length,
      };
    }

    const values = pts.map(val);
    return {
      current: values[values.length - 1],
      min: Math.min(...values),
      max: Math.max(...values),
      avg: values.reduce((a, b) => a + b, 0) / values.length,
    };
  });

  protected currentValue = computed(() => this.stats().current);
  protected minValue = computed(() => this.stats().min);
  protected maxValue = computed(() => this.stats().max);
  protected avgValue = computed(() => this.stats().avg);

  /** Metadata rows for the Metadata tab. */
  protected metadataRows = computed(() => {
    const info = this.instances()[0];
    const pts = this.points();
    const latest = pts.length
      ? [...pts].sort((a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime())[0]
      : null;
    const rows: { label: string; value: string }[] = [];
    rows.push({ label: 'Metric Name', value: this.metricName() });
    rows.push({ label: 'Type', value: this.typeLabel() });
    if (info?.unit) rows.push({ label: 'Unit', value: info.unit });
    if (info?.description) rows.push({ label: 'Description', value: info.description });
    rows.push({ label: 'Services', value: this.services().join(', ') || '—' });
    rows.push({ label: 'Instance Count', value: String(this.instances().length) });
    if (info) {
      rows.push({ label: 'First Seen', value: new Date(info.firstSeen).toLocaleString() });
      rows.push({ label: 'Last Seen', value: new Date(info.lastSeen).toLocaleString() });
      rows.push({ label: 'Data Point Count', value: String(info.dataPointCount) });
    }
    rows.push({ label: 'Current Data Points', value: String(pts.length) });
    if (latest?.flags != null) rows.push({ label: 'Latest Flags', value: String(latest.flags) });
    if (latest?.aggregationTemporality != null) {
      rows.push({ label: 'Aggregation Temporality', value: TEMPORALITY_LABELS[latest.aggregationTemporality] });
    }
    if (latest?.isMonotonic != null) rows.push({ label: 'Is Monotonic', value: String(latest.isMonotonic) });
    if (this.isExpHistogram() && latest) {
      rows.push({ label: 'Scale', value: latest.scale?.toString() ?? 'N/A' });
      rows.push({ label: 'Zero Count', value: latest.zeroCount?.toString() ?? 'N/A' });
      rows.push({ label: 'Positive Offset', value: latest.positiveOffset?.toString() ?? 'N/A' });
      rows.push({ label: 'Negative Offset', value: latest.negativeOffset?.toString() ?? 'N/A' });
    }
    return rows;
  });

  protected exemplars = computed(() =>
    this.points()
      .flatMap((p) => p.exemplars ?? [])
      .filter((e) => e.traceIdHex)
  );

  constructor() {
    effect(() => {
      this.timeRange.range();
      untracked(() => this.loadAll());
    });

    // Persist filter/view/tab state (scoped to the current metric).
    effect(() => {
      savePageState(STATE_KEY, {
        metricName: this.metricName(),
        selectedService: this.selectedService(),
        selectedLabels: this.selectedLabels(),
        showPerService: this.showPerService(),
        showRaw: this.showRaw(),
        activeTab: this.activeTab(),
      });
    });
  }

  ngOnInit(): void {
    this.title.setTitle(`Metric: ${this.metricName()}`);

    // Restore state only if it belongs to the metric we're now viewing.
    const saved = loadPageState(STATE_KEY, {
      metricName: '', selectedService: '', selectedLabels: {} as Record<string, string>,
      showPerService: false, showRaw: false, activeTab: 0,
    });
    if (saved.metricName === this.metricName()) {
      this.selectedService.set(saved.selectedService);
      this.selectedLabels.set(saved.selectedLabels);
      this.showPerService.set(saved.showPerService);
      this.showRaw.set(saved.showRaw);
      this.activeTab.set(saved.activeTab);
    }
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
        // Honor restored filters/view rather than showing the unfiltered series.
        if (this.showPerService() || this.selectedService() || Object.keys(this.selectedLabels()).length > 0) {
          this.reloadSeries();
        } else {
          this.buildChart();
        }
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
    this.minMaxChartOptions.set(null);

    let chartType: 'area' | 'bar' | 'line' = 'area';
    let chartSeries: { name: string; data: [number, number][] }[];

    if (this.isSummary()) {
      // One line per distinct quantile over time.
      chartType = 'line';
      const quantiles = [...new Set(points.flatMap((p) => p.quantiles ?? []))].sort((a, b) => a - b);
      chartSeries = quantiles.map((q) => ({
        name: `P${(q * 100).toFixed(0)}`,
        data: points.map((p) => {
          const idx = p.quantiles?.findIndex((x) => Math.abs(x - q) < 1e-7) ?? -1;
          const v = idx >= 0 ? p.quantileValues?.[idx] ?? null : null;
          return [new Date(p.timestamp).getTime(), v ?? 0] as [number, number];
        }),
      }));
    } else if (this.isHistogram() || this.isExpHistogram()) {
      // Count + Sum trend, plus a separate Min/Max chart.
      chartType = 'line';
      chartSeries = [
        { name: 'Count', data: points.map((p) => [new Date(p.timestamp).getTime(), p.count ?? 0]) },
      ];
      if (points.some((p) => p.sum != null)) {
        chartSeries.push({ name: 'Sum', data: points.map((p) => [new Date(p.timestamp).getTime(), p.sum ?? 0]) });
      }
      this.buildMinMaxChart(points, isDark);
    } else if (this.isDelta()) {
      chartType = 'bar';
      chartSeries = [{ name: s.name, data: points.map((p) => [new Date(p.timestamp).getTime(), val(p)]) }];
    } else if (this.isCounter() && !this.showRaw()) {
      chartType = 'area';
      chartSeries = [{ name: `${s.name} (rate/s)`, data: computeRateSeries(points) }];
    } else {
      chartType = 'area';
      chartSeries = [{ name: s.name, data: points.map((p) => [new Date(p.timestamp).getTime(), val(p)]) }];
    }

    this.chartOptions.set({
      chart: { type: chartType, height: 300, toolbar: { show: false }, background: 'transparent' },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: chartSeries,
      xaxis: { type: 'datetime' },
      stroke: { curve: 'smooth', width: 2 },
      fill: { opacity: chartType === 'area' ? 0.15 : 1 },
      dataLabels: { enabled: false },
      yaxis: { labels: { formatter: (v: number) => v.toFixed(2) } },
      legend: { position: 'top' },
    });
  }

  private buildMinMaxChart(points: MetricDataPoint[], isDark: boolean): void {
    const minMax: { name: string; data: [number, number][] }[] = [];
    if (points.some((p) => p.min != null)) {
      minMax.push({ name: 'Min', data: points.map((p) => [new Date(p.timestamp).getTime(), p.min ?? 0]) });
    }
    if (points.some((p) => p.max != null)) {
      minMax.push({ name: 'Max', data: points.map((p) => [new Date(p.timestamp).getTime(), p.max ?? 0]) });
    }
    if (!minMax.length) { this.minMaxChartOptions.set(null); return; }

    this.minMaxChartOptions.set({
      chart: { type: 'line', height: 220, toolbar: { show: false }, background: 'transparent' },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: minMax,
      xaxis: { type: 'datetime' },
      stroke: { curve: 'smooth', width: 2 },
      dataLabels: { enabled: false },
      legend: { position: 'top' },
      yaxis: { labels: { formatter: (v: number) => v.toFixed(2) } },
    });
  }

  private buildMultiChart(multi: MultiSeriesMetricData): void {
    const isDark = this.theme.isDark();
    const first = multi.series[0]?.points ?? [];
    const counter = isCounterMetric(multi.type, first);
    const delta = isDeltaSum(multi.type, first);
    this.minMaxChartOptions.set(null);

    this.chartOptions.set({
      chart: { type: delta ? 'bar' : 'line', height: 300, toolbar: { show: false }, background: 'transparent' },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: multi.series.map((s) => ({
        name: s.seriesName,
        data: counter && !this.showRaw()
          ? computeRateSeries(s.points)
          : s.points.map((p) => [new Date(p.timestamp).getTime(), val(p)] as [number, number]),
      })),
      xaxis: { type: 'datetime' },
      stroke: { curve: 'smooth', width: 2 },
      dataLabels: { enabled: false },
      legend: { position: 'top' },
    });
  }

  protected fmt(v: number | null | undefined): string {
    return v != null ? v.toFixed(3) : '—';
  }

  protected fmtStat(v: number | null | undefined): string {
    return v != null ? `${v.toFixed(3)}${this.statsUnitSuffix()}` : '—';
  }

  protected updateLabelFilter(key: string, value: string): void {
    this.selectedLabels.update((l) => ({ ...l, [key]: value }));
    this.reloadSeries();
  }

  /** Build and download a per-type CSV of the current series. Mirrors Blazor's ExportData. */
  protected exportCsv(): void {
    const pts = this.points();
    if (!pts.length) return;
    const type = this.metricType();

    const ser = (v: unknown): string => (v == null ? '' : JSON.stringify(v));
    const ts = (p: MetricDataPoint) => new Date(p.timestamp).toLocaleString();

    let headers: string[];
    let row: (p: MetricDataPoint) => (string | number)[];

    switch (type) {
      case MetricType.Histogram:
        headers = ['Timestamp', 'Count', 'Sum', 'Min', 'Max', 'BucketCounts', 'BucketBounds', 'Attributes'];
        row = (p) => [ts(p), p.count ?? '', p.sum ?? '', p.min ?? '', p.max ?? '', ser(p.bucketCounts), ser(p.bucketBounds), ser(p.attributes)];
        break;
      case MetricType.ExponentialHistogram:
        headers = ['Timestamp', 'Count', 'Sum', 'Min', 'Max', 'Attributes'];
        row = (p) => [ts(p), p.count ?? '', p.sum ?? '', p.min ?? '', p.max ?? '', ser(p.attributes)];
        break;
      case MetricType.Summary:
        headers = ['Timestamp', 'Count', 'Sum', 'Quantiles', 'QuantileValues', 'Attributes'];
        row = (p) => [ts(p), p.count ?? '', p.sum ?? '', ser(p.quantiles), ser(p.quantileValues), ser(p.attributes)];
        break;
      default: // Gauge / Sum
        headers = ['Timestamp', 'Value', 'Attributes'];
        row = (p) => [ts(p), p.doubleValue ?? p.intValue ?? '', ser(p.attributes)];
    }

    const escape = (v: string | number): string => {
      const s = String(v);
      return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
    };

    const lines = [headers.join(',')];
    for (const p of pts) lines.push(row(p).map(escape).join(','));
    const csv = lines.join('\n');

    const stamp = new Date().toISOString().replace(/[:.]/g, '').slice(0, 15);
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${this.metricName()}_${stamp}Z.csv`;
    a.click();
    URL.revokeObjectURL(url);
  }
}
