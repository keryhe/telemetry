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
import { MatTooltipModule } from '@angular/material/tooltip';
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
  buildHistogramBarFromWindows, buildHistogramHeatmapFromWindows, buildRadialGauge, buildShareDonut,
  chartGrid, computeRateSeries,
  HistogramWindow, histogramQuantile,
  isCounterMetric, isDeltaSum, normalizeExpHistogramSeries, timeRangeZoom,
} from '../../../shared/utils/chart.utils';
import { loadPageState, savePageState } from '../../../shared/utils/page-state';

const STATE_KEY = 'state.metricDetail';

type GroupMode = 'service' | 'labels';
type AggMode = 'none' | AggregateFn;
/** How the scalar (gauge/sum) chart is drawn; distributions ignore this. */
type ChartStyle = 'timeseries' | 'stacked' | 'share' | 'dial';

/** Icon + label for each chart-style tile (Material glyphs chosen to read distinctly at 24px). */
const CHART_STYLE_META: Record<ChartStyle, { icon: string; label: string }> = {
  timeseries: { icon: 'show_chart', label: 'Time series' },
  stacked: { icon: 'stacked_line_chart', label: 'Stacked' },
  share: { icon: 'donut_large', label: 'Share' },
  dial: { icon: 'speed', label: 'Dial' },
};

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
    MatSelectModule, MatProgressBarModule, MatTooltipModule, NgApexchartsModule,
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
  /** Chart shape for scalar (gauge/sum) metrics; guarded to a value valid for the current type. */
  protected chartStyle = signal<ChartStyle>('timeseries');

  protected readonly aggModes = AGG_MODES;
  protected readonly chartStyleMeta = CHART_STYLE_META;

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
   * A gauge whose unit gives the dial a real 0–100 / 0–1 bound (`%` or the OTLP dimensionless
   * ratio `1`). Only these get a Dial; unbounded gauges (bytes, temperature, …) have no honest
   * reference for a radial fill.
   */
  protected isBoundedGauge = computed(() => {
    if (this.metricType() !== MetricType.Gauge) return false;
    const unit = this.instances()[0]?.unit ?? '';
    return unit === '%' || unit === '1';
  });

  /**
   * Chart-style tiles available for the current metric type. Empty for distributions (selector
   * hidden). Gauges are instantaneous levels: they don't stack, and share-of-total is a Sum
   * concept, so they only add a Dial when bounded. Sums get stacked + share composition views.
   */
  protected chartStyles = computed<ChartStyle[]>(() => {
    if (this.isDistribution()) return [];
    if (this.metricType() === MetricType.Gauge) {
      return this.isBoundedGauge() ? ['timeseries', 'dial'] : ['timeseries'];
    }
    return ['timeseries', 'stacked', 'share'];
  });
  /** The per-series grouping / aggregate controls only make sense for the time-series & stacked views. */
  protected showSeriesControls = computed(() =>
    this.chartStyle() === 'timeseries' || this.chartStyle() === 'stacked');

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
        chartStyle: this.chartStyle(),
      });
    });
  }

  ngOnInit(): void {
    this.title.setTitle(`Metric: ${this.metricName()}`);

    // Restore state only if it belongs to the metric we're now viewing.
    const saved = loadPageState(STATE_KEY, {
      metricName: '', selectedService: '', selectedLabels: {} as Record<string, string>,
      groupMode: 'labels' as GroupMode, aggMode: 'none' as AggMode, showRaw: false, activeTab: 0,
      chartStyle: 'timeseries' as ChartStyle,
    });
    if (saved.metricName === this.metricName()) {
      this.selectedService.set(saved.selectedService);
      this.selectedLabels.set(saved.selectedLabels);
      // Coerce any legacy persisted mode (e.g. the removed 'single') to a valid one.
      this.groupMode.set(saved.groupMode === 'service' ? 'service' : 'labels');
      this.aggMode.set(saved.aggMode);
      this.showRaw.set(saved.showRaw);
      this.activeTab.set(saved.activeTab);
      // buildMultiChart guards this against a style invalid for the metric's (not-yet-loaded) type.
      this.chartStyle.set(saved.chartStyle);
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
    const { start: rangeStart, end: rangeEnd } = this.timeRange.range();
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
      // Pin the axis to the header-selected window so the chart tracks that range (not the data extent).
      xaxis: { type: 'datetime', min: rangeStart.getTime(), max: rangeEnd.getTime(), labels: { datetimeUTC: false } },
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
      xaxis: { type: 'datetime', min: start.getTime(), max: end.getTime(), labels: { datetimeUTC: false } },
      stroke: { curve: 'smooth', width: 2 },
      dataLabels: { enabled: false },
      grid: chartGrid(isDark),
      legend: { position: 'top' },
      yaxis: { labels: { formatter: (v: number) => v.toFixed(2) }, title: { text: '/s' } },
    });
  }

  private buildMultiChart(multi: MultiSeriesMetricData): void {
    // A restored/foreign style not valid for this metric type falls back to the time-series view.
    if (!this.chartStyles().includes(this.chartStyle())) this.chartStyle.set('timeseries');

    const isDark = this.theme.isDark();
    const first = multi.series[0]?.points ?? [];
    const counter = isCounterMetric(multi.type, first);
    const delta = isDeltaSum(multi.type, first);
    const asRate = counter && !this.showRaw();
    this.throughputChartOptions.set(null);
    this.bucketBarOptions.set(null);
    this.heatmapOptions.set(null);

    // Composition views collapse each series to a single value — a different shape entirely.
    if (this.chartStyle() === 'share') { this.buildShareChart(multi, counter, delta, isDark); return; }
    if (this.chartStyle() === 'dial') { this.buildDialChart(isDark); return; }

    // Time-series & Stacked share the same series computation; Stacked aligns onto the shared grid
    // (via aggregateSeries) so the bands sum correctly, and turns on chart.stacked.
    const stacked = this.chartStyle() === 'stacked';
    // Stacked ignores the collapse-to-one-line Aggregate (its control is hidden) so bands remain.
    const agg = stacked ? 'none' : this.aggMode();
    const { start, end } = this.timeRange.range();

    let chartSeries: { name: string; data: [number, number][] }[];
    if (this.groupMode() === 'service') {
      // One line per service, folded by the effective function (sum for counters/rate; avg for
      // gauges by default; overridable via the Aggregate control). Already grid-aligned.
      chartSeries = this.foldByService(multi.series, asRate, this.effectiveFold());
    } else if (agg !== 'none') {
      // Collapse all grouped series into one aggregated line (rate-then-aggregate for counters).
      const data = aggregateSeries(multi.series, start, end, agg, asRate);
      chartSeries = [{ name: `${agg} of ${multi.series.length} series`, data }];
    } else {
      chartSeries = this.capTopN(multi.series, asRate, stacked);
    }

    // Delta sums render as bars only in the per-series time-series view; per-service folds them into
    // a line. Stacked uses area for rates/levels and bars for delta/raw values.
    const useBar = delta && agg === 'none' && this.groupMode() === 'labels';
    const chartType = stacked ? (asRate ? 'area' : 'bar') : (useBar ? 'bar' : 'line');

    this.chartOptions.set({
      chart: {
        type: chartType,
        stacked,
        height: 300, toolbar: { show: false }, background: 'transparent',
        ...this.zoomChart(),
      },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: chartSeries,
      // Pin the axis to the header-selected window so the chart always reflects that range,
      // rather than auto-fitting to the data extent (which made sparse/single-series gauges
      // appear to span a different, narrower time frame than the header).
      xaxis: { type: 'datetime', min: start.getTime(), max: end.getTime(), labels: { datetimeUTC: false } },
      stroke: { curve: 'smooth', width: stacked && asRate ? 1 : 2 },
      fill: { opacity: stacked && asRate ? 0.7 : 1 },
      dataLabels: { enabled: false },
      yaxis: { labels: { formatter: (v: number) => v.toFixed(2) } },
      grid: chartGrid(isDark),
      legend: { position: 'top' },
    });
  }

  /** Latest-timestamp raw value of a series (for gauge snapshots / dial). */
  private latestVal(points: MetricDataPoint[]): number {
    if (!points.length) return 0;
    const latest = points.reduce((a, b) =>
      new Date(b.timestamp).getTime() > new Date(a.timestamp).getTime() ? b : a);
    return val(latest);
  }

  /** Reset-safe total increase of a cumulative counter over its points (Σ positive consecutive deltas). */
  private windowIncrease(points: MetricDataPoint[]): number {
    let sum = 0;
    for (let i = 1; i < points.length; i++) {
      const d = val(points[i]) - val(points[i - 1]);
      if (d > 0) sum += d;
    }
    return sum;
  }

  /** Group raw series by service name (for the per-service slice/band grouping). */
  private groupByService(series: NamedMetricSeries[]): { name: string; list: NamedMetricSeries[] }[] {
    const byService = new Map<string, NamedMetricSeries[]>();
    for (const s of series) {
      const key = s.serviceName || 'unknown';
      const list = byService.get(key) ?? [];
      list.push(s);
      byService.set(key, list);
    }
    return [...byService.entries()].map(([name, list]) => ({ name, list }));
  }

  /**
   * Donut of share-of-total, for Sum metrics only (composition of a real total). Uses the window
   * total: true increase for cumulative counters, Σ values for deltas, latest value for raw/
   * non-monotonic sums. Slices follow the per-series / per-service grouping; the top MAX_SERIES are
   * kept and the rest folded into "others".
   */
  private buildShareChart(multi: MultiSeriesMetricData, counter: boolean, delta: boolean, isDark: boolean): void {
    const groups = this.groupMode() === 'service'
      ? this.groupByService(multi.series)
      : multi.series.map((s) => ({ name: s.seriesName, list: [s] }));

    const groupValue = (list: NamedMetricSeries[]): number => list.reduce((acc, s) => {
      if (delta) return acc + s.points.reduce((a, p) => a + val(p), 0);
      if (counter && !this.showRaw()) return acc + this.windowIncrease(s.points);
      return acc + this.latestVal(s.points); // raw counter / non-monotonic sum
    }, 0);

    const slices = groups.map((g) => ({ name: g.name, value: groupValue(g.list) }));
    const ranked = [...slices].sort((a, b) => b.value - a.value);
    const kept = ranked.slice(0, MAX_SERIES);
    const rest = ranked.slice(MAX_SERIES);
    if (rest.length) {
      kept.push({ name: `others (${rest.length})`, value: rest.reduce((a, s) => a + s.value, 0) });
    }

    this.chartOptions.set(buildShareDonut(kept, isDark) ?? {});
  }

  /**
   * Radial gauge of the whole-metric current value against its unit's natural bound. Only offered
   * for bounded gauges (see {@link isBoundedGauge}): `%` → 0–100, ratio `1` → 0–1.
   */
  private buildDialChart(isDark: boolean): void {
    const unit = this.instances()[0]?.unit ?? '';
    const value = this.stats().current ?? 0;
    const max = unit === '%' ? 100 : 1; // '1' (dimensionless ratio) or any other bounded unit
    this.chartOptions.set(buildRadialGauge(value, max, this.metricName(), isDark, unit === '1' ? '' : unit));
  }

  /**
   * Renders the MAX_SERIES largest series individually and folds the remainder into a single
   * summed "others" line, keeping the legend readable when a metric has many label sets. When
   * `aligned` (stacked view), each series is resampled onto the shared grid so the bands stack.
   */
  private capTopN(series: NamedMetricSeries[], asRate: boolean, aligned = false): { name: string; data: [number, number][] }[] {
    const { start, end } = this.timeRange.range();
    const toData = (s: NamedMetricSeries): [number, number][] =>
      aligned ? aggregateSeries([s], start, end, 'sum', asRate)
             : asRate ? computeRateSeries(s.points)
             : s.points.map((p) => [new Date(p.timestamp).getTime(), val(p)] as [number, number]);

    if (series.length <= MAX_SERIES) {
      return series.map((s) => ({ name: s.seriesName, data: toData(s) }));
    }

    const peak = (data: [number, number][]) => data.reduce((m, [, v]) => Math.max(m, Math.abs(v)), 0);
    const ranked = [...series].sort((a, b) => peak(toData(b)) - peak(toData(a)));
    const kept = ranked.slice(0, MAX_SERIES);
    const rest = ranked.slice(MAX_SERIES);

    const out = kept.map((s) => ({ name: s.seriesName, data: toData(s) }));
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

  /** Chart style only re-draws the already-loaded grouped series — no refetch needed. */
  protected onChartStyleChange(style: ChartStyle): void {
    this.chartStyle.set(style);
    const multi = this.multiSeries();
    if (multi && !this.isDistribution()) this.buildMultiChart(multi);
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
