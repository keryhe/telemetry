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
  AggregationTemporality, MetricDataPoint, MetricInfo, MetricSeries, MetricType,
  MultiSeriesMetricData, NamedMetricSeries,
} from '../../../core/models/metric.models';
import { StatCardComponent } from '../../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import {
  AggregateFn, aggregateHistogramWindows, aggregateSeries, aggregateSummaryWindows,
  buildHistogramBarFromWindows, buildHistogramHeatmapFromWindows, chartGrid, computeRateSeries,
  HistogramWindow, histogramQuantile,
  isCounterMetric, isDeltaSum, normalizeExpHistogramSeries, timeRangeZoom,
} from '../../../shared/utils/chart.utils';
import { loadPageState, savePageState } from '../../../shared/utils/page-state';

const STATE_KEY = 'state.metricDetail';

type GroupMode = 'service' | 'labels';
type AggMode = 'none' | AggregateFn;

/** Aggregation choices for the grouped-series control. */
const AGG_MODES: { value: AggMode; label: string }[] = [
  { value: 'none', label: 'None' }, { value: 'sum', label: 'Sum' },
  { value: 'avg', label: 'Avg' }, { value: 'min', label: 'Min' }, { value: 'max', label: 'Max' },
];

/** Series rendered individually before the rest are folded into an "others" line. */
const MAX_SERIES = 8;

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
  /** Scalar-metric grouping: 'labels' = one line per full label set, 'service' = one line per service. */
  protected groupMode = signal<GroupMode>('labels');
  /** Cross-series aggregation applied in a grouping mode; 'none' keeps per-series lines. */
  protected aggMode = signal<AggMode>('none');
  protected showRaw = signal(false);
  protected activeTab = signal(0);

  protected readonly aggModes = AGG_MODES;

  protected metricType = computed(() => this.instances()[0]?.type ?? MetricType.Gauge);

  /**
   * Points feeding the stat cards / metadata / exemplars / export. For scalar metrics this is the
   * largest grouped series (a real single time series) rather than an interleaved merge, so counter
   * rates and min/max stay meaningful. Distribution metrics keep the merged series, whose per-point
   * percentiles/heatmap the chart derives directly.
   */
  protected points = computed(() => {
    if (this.isDistribution()) return this.series()?.points ?? [];
    const grouped = this.multiSeries()?.series ?? [];
    if (!grouped.length) return this.series()?.points ?? [];
    return grouped.reduce((a, b) => (b.points.length > a.points.length ? b : a)).points;
  });
  protected isCounter = computed(() => isCounterMetric(this.metricType(), this.points()));
  protected isDelta = computed(() => isDeltaSum(this.metricType(), this.points()));
  protected isHistogram = computed(() => this.metricType() === MetricType.Histogram);
  protected isExpHistogram = computed(() => this.metricType() === MetricType.ExponentialHistogram);
  protected isSummary = computed(() => this.metricType() === MetricType.Summary);
  /** Distribution metrics render derived percentiles/heatmap (via buildChart), not per-series grouping. */
  protected isDistribution = computed(() => this.isHistogram() || this.isExpHistogram() || this.isSummary());

  /**
   * Single windowed aggregate for an explicit histogram (across all series). Percentiles, throughput,
   * mean, min/max and the heatmap all derive from this, keeping them self-consistent.
   */
  protected histogramWindows = computed(() => {
    const multi = this.multiSeries();
    if (!multi) return null;
    const { start, end } = this.timeRange.range();
    if (this.isHistogram()) return aggregateHistogramWindows(multi.series, start, end);
    // Exp histograms: normalize onto one shared bucket schema, then use the identical windowing.
    if (this.isExpHistogram()) return aggregateHistogramWindows(normalizeExpHistogramSeries(multi.series), start, end);
    return null;
  });

  /** Additive (count/sum) window aggregate for summaries — feeds the Throughput/Mean view. */
  protected summaryWindows = computed(() => {
    if (!this.isSummary()) return null;
    const multi = this.multiSeries();
    if (!multi) return null;
    const { start, end } = this.timeRange.range();
    return aggregateSummaryWindows(multi.series, start, end);
  });

  /** Representative single series for summary quantiles (quantiles can't be aggregated across series). */
  protected summarySeries = computed(() => {
    const series = this.multiSeries()?.series ?? [];
    if (!series.length) return null;
    return series.reduce((a, b) => (b.points.length > a.points.length ? b : a));
  });

  /** Default cross-series fold by type: gauges average (levels), all sum types sum (additive). */
  protected defaultFold = computed<AggregateFn>(() => (this.metricType() === MetricType.Gauge ? 'avg' : 'sum'));
  /** Fold actually applied: an explicit Aggregate choice, else the type default. */
  protected effectiveFold = computed<AggregateFn>(() => (this.aggMode() === 'none' ? this.defaultFold() : this.aggMode() as AggregateFn));

  /**
   * Whole-metric aggregate for scalar (gauge/sum) stat cards: fold every series with effectiveFold
   * (rate-first for counters) rather than picking one series. Empty for distribution metrics.
   */
  protected scalarAggregateData = computed<[number, number][]>(() => {
    if (this.isDistribution()) return [];
    const grouped = this.multiSeries()?.series ?? [];
    const list = grouped.length ? grouped : (this.series() ? [{ points: this.series()!.points }] : []);
    if (!list.length) return [];
    const { start, end } = this.timeRange.range();
    return aggregateSeries(list, start, end, this.effectiveFold(), this.isCounter() && !this.showRaw());
  });
  protected typeLabel = computed(() => TYPE_LABELS[this.metricType()] ?? 'Unknown');

  /** Stats are rates only for a cumulative counter when not showing raw values. */
  protected statsAreRates = computed(() => this.isCounter() && !this.showRaw());
  protected statsUnitSuffix = computed(() => (this.statsAreRates() ? '/s' : ''));

  protected services = computed(() =>
    [...new Set(this.instances().map((i) => i.serviceName).filter(Boolean) as string[])]
  );
  protected labelKeys = computed(() => Object.keys(this.labels()));

  protected chartOptions = signal<ApexOptions>({});
  /** Windowed throughput (req/s) trend for histogram / exp-histogram / summary metrics. */
  protected throughputChartOptions = signal<ApexOptions | null>(null);
  /** Latest-window bucket distribution bar chart for histogram / exp-histogram metrics. */
  protected bucketBarOptions = signal<ApexOptions | null>(null);
  /** Bucket-distribution heatmap for histogram / exponential-histogram metrics. */
  protected heatmapOptions = signal<ApexOptions | null>(null);

  /** Percentiles plotted for histogram metrics. */
  private static readonly PERCENTILES: { q: number; label: string }[] = [
    { q: 0.5, label: 'p50' }, { q: 0.95, label: 'p95' }, { q: 0.99, label: 'p99' },
  ];

  /** Latest-point quantile snapshot for summary metrics (from the representative series). */
  protected summarySnapshot = computed(() => {
    const pts = this.summarySeries()?.points ?? [];
    if (!pts.length) return [];
    const latest = [...pts].sort((a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime())[0];
    const qs = latest.quantiles, vs = latest.quantileValues;
    if (!qs || !vs) return [];
    return qs.map((q, i) => ({ label: `P${(q * 100).toFixed(0)}`, value: vs[i] }))
      .filter((r) => r.value != null);
  });

  /** Stats: histogram window aggregate; scalar whole-metric fold; exp-hist/summary raw per-point. */
  private stats = computed(() => {
    const empty = { current: null, min: null, max: null, avg: null };

    if (this.isHistogram() || this.isExpHistogram()) {
      // Aggregate over the whole visible window across all series: Mean, Count, and a min/max envelope.
      const windows = (this.histogramWindows()?.windows ?? []).filter((w) => w.total > 0);
      if (!windows.length) return empty;
      const totalCount = windows.reduce((a, w) => a + w.total, 0);
      const totalSum = windows.reduce((a, w) => a + w.sum, 0);
      const mins = windows.map((w) => w.min).filter((v): v is number => v != null);
      const maxs = windows.map((w) => w.max).filter((v): v is number => v != null);
      return {
        current: totalCount > 0 ? totalSum / totalCount : null, // Mean
        min: mins.length ? Math.min(...mins) : null,
        max: maxs.length ? Math.max(...maxs) : null,
        avg: totalCount, // Count card
      };
    }

    if (this.isSummary()) {
      // Only the additive count/sum are aggregatable: window Mean and total Count (no min/max).
      const windows = (this.summaryWindows() ?? []).filter((w) => w.total > 0);
      if (!windows.length) return empty;
      const totalCount = windows.reduce((a, w) => a + w.total, 0);
      const totalSum = windows.reduce((a, w) => a + w.sum, 0);
      return { current: totalCount > 0 ? totalSum / totalCount : null, min: null, max: null, avg: totalCount };
    }

    // Scalar (gauge/sum): stats over the whole-metric aggregate (fold across all series; rate-first
    // for counters), so they describe the metric rather than the largest single series.
    const agg = this.scalarAggregateData();
    if (!agg.length) return empty;
    const vals = agg.map(([, v]) => v);
    return {
      current: vals[vals.length - 1],
      min: Math.min(...vals),
      max: Math.max(...vals),
      avg: vals.reduce((a, b) => a + b, 0) / vals.length,
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
    // Slide relative preset windows to "now" on (re)entry so navigating back refreshes.
    this.timeRange.refreshRelativeWindow();

    effect(() => {
      this.timeRange.range();
      untracked(() => this.loadAll());
    });

    // Rebuild the charts when the theme toggles so colors track light/dark without a refresh.
    effect(() => {
      this.theme.isDark();
      untracked(() => {
        const multi = this.multiSeries();
        if (!multi) return; // nothing loaded yet
        if (this.isDistribution()) this.buildChart();
        else this.buildMultiChart(multi);
      });
    });

    // Persist filter/view/tab state (scoped to the current metric).
    effect(() => {
      savePageState(STATE_KEY, {
        metricName: this.metricName(),
        selectedService: this.selectedService(),
        selectedLabels: this.selectedLabels(),
        groupMode: this.groupMode(),
        aggMode: this.aggMode(),
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
      groupMode: 'labels' as GroupMode, aggMode: 'none' as AggMode, showRaw: false, activeTab: 0,
    });
    if (saved.metricName === this.metricName()) {
      this.selectedService.set(saved.selectedService);
      this.selectedLabels.set(saved.selectedLabels);
      // Coerce any legacy persisted mode (e.g. the removed 'single') to a valid one.
      this.groupMode.set(saved.groupMode === 'service' ? 'service' : 'labels');
      this.aggMode.set(saved.aggMode);
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
        // Every type now loads from the grouped path (honoring any restored service/label filters).
        this.reloadSeries();
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
    const labelFilters = Object.keys(this.selectedLabels()).length > 0 ? this.selectedLabels() : undefined;

    // Distributions (histogram, exp-histogram, summary): fetch the real per-series data for a correct
    // windowed aggregate, but keep a flattened merged series so stats/exemplars/export/Metadata work.
    if (this.isDistribution()) {
      this.api.getGroupedSeries({ metricName: name, start, end, metricId, labelFilters }).subscribe((multi) => {
        this.multiSeries.set(multi);
        this.series.set(this.flattenSeries(multi));
        this.buildChart();
      });
      return;
    }

    // Scalar metrics: fetch the real per-(service, label-set) series; groupMode only controls how
    // buildMultiChart folds them (per service vs. per full label set).
    this.api.getGroupedSeries({ metricName: name, start, end, metricId, labelFilters }).subscribe((multi) => {
      this.multiSeries.set(multi);
      this.buildMultiChart(multi);
    });
  }

  /** Collapse grouped series back into one time-sorted merged series (for stats/sub-charts/export). */
  private flattenSeries(multi: MultiSeriesMetricData): MetricSeries {
    const points = multi.series
      .flatMap((s) => s.points)
      .sort((a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime());
    return { name: multi.name, type: multi.type, labels: {}, points };
  }

  /** Wheel-zoom off + drag-select drives the shared time-range picker (datetime charts). */
  private zoomChart() {
    return timeRangeZoom((start, end) => this.timeRange.setCustom(start, end));
  }

  private buildChart(): void {
    const s = this.series();
    if (!s) return;
    const isDark = this.theme.isDark();
    const points = s.points;
    this.throughputChartOptions.set(null);
    this.bucketBarOptions.set(null);
    this.heatmapOptions.set(null);

    let chartType: 'area' | 'bar' | 'line' = 'area';
    let chartSeries: { name: string; data: [number, number][] }[];
    // Per-series stroke for the main chart; histograms override it to render Max faint/dashed.
    let stroke: ApexOptions['stroke'] = { curve: 'smooth', width: 2 };

    if (this.isSummary()) {
      // Quantiles can't be aggregated across series — plot them for one representative series. The
      // additive count/sum become a windowed Throughput aggregate across all series.
      chartType = 'line';
      const pts = this.summarySeries()?.points ?? [];
      const quantiles = [...new Set(pts.flatMap((p) => p.quantiles ?? []))].sort((a, b) => a - b);
      chartSeries = quantiles.map((q) => ({
        name: `P${(q * 100).toFixed(0)}`,
        data: pts.map((p) => {
          const idx = p.quantiles?.findIndex((x) => Math.abs(x - q) < 1e-7) ?? -1;
          const v = idx >= 0 ? p.quantileValues?.[idx] ?? null : null;
          return [new Date(p.timestamp).getTime(), v ?? 0] as [number, number];
        }),
      }));
      const sw = this.summaryWindows();
      if (sw) {
        const { start, end } = this.timeRange.range();
        this.buildThroughputChart(sw, start, end, isDark);
      }
    } else if ((this.isHistogram() || this.isExpHistogram()) && this.histogramWindows()) {
      // Histogram / exp-histogram: every view derives from one windowed aggregate across all series,
      // so the percentiles, throughput, bucket distribution and heatmap stay mutually consistent.
      chartType = 'line';
      const { start, end } = this.timeRange.range();
      const { bounds, windows } = this.histogramWindows()!;
      const nonEmpty = windows.filter((w) => w.total > 0);
      chartSeries = MetricDetailComponent.PERCENTILES.map(({ q, label }) => ({
        name: label,
        data: nonEmpty
          .map((w) => [w.edge, histogramQuantile(w.counts, bounds, q)] as [number, number])
          .filter(([, v]) => Number.isFinite(v)),
      })).filter((s2) => s2.data.length > 0);
      // Fold Max into the percentile chart as a faint dashed line (worst-case context).
      const maxData = nonEmpty
        .filter((w) => w.max != null)
        .map((w) => [w.edge, w.max!] as [number, number]);
      if (maxData.length) {
        chartSeries.push({ name: 'Max', data: maxData });
        const n = chartSeries.length;
        stroke = {
          curve: 'smooth',
          width: [...Array(n - 1).fill(2), 1],
          dashArray: [...Array(n - 1).fill(0), 6],
        };
      }
      this.buildThroughputChart(windows, start, end, isDark);
      this.bucketBarOptions.set(buildHistogramBarFromWindows(bounds, windows, isDark));
      const heatmap = buildHistogramHeatmapFromWindows(bounds, windows, isDark);
      this.heatmapOptions.set(heatmap ? { ...heatmap, chart: { ...heatmap.chart!, ...this.zoomChart() } } : null);
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
      chart: { type: chartType, height: 300, toolbar: { show: false }, background: 'transparent', ...this.zoomChart() },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: chartSeries,
      xaxis: { type: 'datetime', labels: { datetimeUTC: false } },
      stroke,
      fill: { opacity: chartType === 'area' ? 0.15 : 1 },
      dataLabels: { enabled: false },
      yaxis: { labels: { formatter: (v: number) => v.toFixed(2) } },
      grid: chartGrid(isDark),
      legend: { position: 'top' },
    });
  }

  /** Explicit-histogram / summary detail: windowed throughput (req/s), aggregated across series. */
  private buildThroughputChart(windows: HistogramWindow[], start: Date, end: Date, isDark: boolean): void {
    const intervals = Math.max(1, windows.length - 1);
    const windowSec = (end.getTime() - start.getTime()) / intervals / 1000;
    const nonEmpty = windows.filter((w) => w.total > 0);
    if (!nonEmpty.length) { this.throughputChartOptions.set(null); return; }

    const throughput = nonEmpty.map((w) => [w.edge, windowSec > 0 ? w.total / windowSec : 0] as [number, number]);

    this.throughputChartOptions.set({
      chart: { type: 'line', height: 220, toolbar: { show: false }, background: 'transparent', ...this.zoomChart() },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: [{ name: 'Throughput (/s)', data: throughput }],
      xaxis: { type: 'datetime', labels: { datetimeUTC: false } },
      stroke: { curve: 'smooth', width: 2 },
      dataLabels: { enabled: false },
      grid: chartGrid(isDark),
      legend: { position: 'top' },
      yaxis: { labels: { formatter: (v: number) => v.toFixed(2) }, title: { text: '/s' } },
    });
  }

  private buildMultiChart(multi: MultiSeriesMetricData): void {
    const isDark = this.theme.isDark();
    const first = multi.series[0]?.points ?? [];
    const counter = isCounterMetric(multi.type, first);
    const delta = isDeltaSum(multi.type, first);
    const asRate = counter && !this.showRaw();
    const agg = this.aggMode();
    this.throughputChartOptions.set(null);
    this.bucketBarOptions.set(null);
    this.heatmapOptions.set(null);

    let chartSeries: { name: string; data: [number, number][] }[];
    if (this.groupMode() === 'service') {
      // One line per service, folded by the effective function (sum for counters/rate; avg for
      // gauges by default; overridable via the Aggregate control).
      chartSeries = this.foldByService(multi.series, asRate, this.effectiveFold());
    } else if (agg !== 'none') {
      // Collapse all grouped series into one aggregated line (rate-then-aggregate for counters).
      const { start, end } = this.timeRange.range();
      const data = aggregateSeries(multi.series, start, end, agg, asRate);
      chartSeries = [{ name: `${agg} of ${multi.series.length} series`, data }];
    } else {
      chartSeries = this.capTopN(multi.series, asRate);
    }

    // Delta sums render as bars only in the per-series view; per-service folds them into a line.
    const useBar = delta && agg === 'none' && this.groupMode() === 'labels';

    this.chartOptions.set({
      chart: {
        type: useBar ? 'bar' : 'line',
        height: 300, toolbar: { show: false }, background: 'transparent',
        ...this.zoomChart(),
      },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: chartSeries,
      xaxis: { type: 'datetime', labels: { datetimeUTC: false } },
      stroke: { curve: 'smooth', width: 2 },
      dataLabels: { enabled: false },
      grid: chartGrid(isDark),
      legend: { position: 'top' },
    });
  }

  /**
   * Renders the MAX_SERIES largest series individually and folds the remainder into a single
   * summed "others" line, keeping the legend readable when a metric has many label sets.
   */
  private capTopN(series: NamedMetricSeries[], asRate: boolean): { name: string; data: [number, number][] }[] {
    const toData = (s: NamedMetricSeries): [number, number][] =>
      asRate ? computeRateSeries(s.points)
             : s.points.map((p) => [new Date(p.timestamp).getTime(), val(p)] as [number, number]);

    if (series.length <= MAX_SERIES) {
      return series.map((s) => ({ name: s.seriesName, data: toData(s) }));
    }

    const peak = (data: [number, number][]) => data.reduce((m, [, v]) => Math.max(m, Math.abs(v)), 0);
    const ranked = [...series].sort((a, b) => peak(toData(b)) - peak(toData(a)));
    const kept = ranked.slice(0, MAX_SERIES);
    const rest = ranked.slice(MAX_SERIES);

    const out = kept.map((s) => ({ name: s.seriesName, data: toData(s) }));
    const { start, end } = this.timeRange.range();
    out.push({ name: `others (${rest.length})`, data: aggregateSeries(rest, start, end, 'sum', asRate) });
    return out;
  }

  /**
   * Per-service view: collapse each service's real series into one line via `foldFn` (rate-first for
   * counters — Grafana's `<fold> by (service)`). Caps to MAX_SERIES services, folding the remainder
   * (by peak) into a single "others" line so the legend stays readable.
   */
  private foldByService(series: NamedMetricSeries[], asRate: boolean, foldFn: AggregateFn): { name: string; data: [number, number][] }[] {
    const { start, end } = this.timeRange.range();
    const byService = new Map<string, NamedMetricSeries[]>();
    for (const s of series) {
      const key = s.serviceName || 'unknown';
      const list = byService.get(key) ?? [];
      list.push(s);
      byService.set(key, list);
    }

    const fold = (list: NamedMetricSeries[]) => aggregateSeries(list, start, end, foldFn, asRate);

    if (byService.size <= MAX_SERIES) {
      return [...byService.entries()].map(([name, list]) => ({ name, data: fold(list) }));
    }

    const peak = (data: [number, number][]) => data.reduce((m, [, v]) => Math.max(m, Math.abs(v)), 0);
    const ranked = [...byService.entries()]
      .map(([name, list]) => ({ name, list, data: fold(list) }))
      .sort((a, b) => peak(b.data) - peak(a.data));

    const out = ranked.slice(0, MAX_SERIES).map(({ name, data }) => ({ name, data }));
    const rest = ranked.slice(MAX_SERIES);
    out.push({ name: `others (${rest.length})`, data: fold(rest.flatMap((r) => r.list)) });
    return out;
  }

  protected fmt(v: number | null | undefined): string {
    return v != null ? v.toFixed(3) : '—';
  }

  protected fmtStat(v: number | null | undefined): string {
    return v != null ? `${v.toFixed(3)}${this.statsUnitSuffix()}` : '—';
  }

  /** Aggregation/top-N only re-shapes the already-loaded grouped series — no refetch needed. */
  protected onAggModeChange(mode: AggMode): void {
    this.aggMode.set(mode);
    const multi = this.multiSeries();
    if (multi) this.buildMultiChart(multi);
  }

  /** Current selection for a label key; '' (the "All" option) when no filter is set. */
  protected labelValue(key: string): string {
    return this.selectedLabels()[key] ?? '';
  }

  protected updateLabelFilter(key: string, value: string): void {
    this.selectedLabels.update((l) => {
      const next = { ...l };
      if (value) next[key] = value; else delete next[key];
      return next;
    });
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
