import { TenantService } from '../../../core/services/tenant.service';
import {
  AfterViewInit, Component, ElementRef, Input, OnDestroy, OnInit, ViewChild,
  computed, effect, inject, signal, untracked,
} from '@angular/core';
import { DatePipe, DecimalPipe, KeyValuePipe, SlicePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { forkJoin, Subscription } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { NgApexchartsModule } from 'ng-apexcharts';
import type { ApexOptions } from 'ng-apexcharts';

import { MetricsApiService } from '../../../core/services/api/metrics-api.service';
import { ResourcesApiService } from '../../../core/services/api/resources-api.service';
import { TimeRangeService } from '../../../core/services/time-range.service';
import { ThemeService } from '../../../core/services/theme.service';
import { CapabilitiesService } from '../../../core/services/capabilities.service';
import {
  ExemplarModel, MetricBucketPoint, MetricExemplar, MetricExemplarPage,
  MetricInfo, MetricSeriesResult, MetricType, TYPE_LABELS, getTypeColor,
} from '../../../core/models/metric.models';
import { StatCardComponent } from '../../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import {
  buildHistogramBarFromBuckets, buildHistogramHeatmapFromBuckets, buildRadialGauge, buildShareDonut,
  chartGrid, foldPointsAcrossGroups, formatUnitLabel, formatUnitValue, histogramQuantile, sumDefined, timeRangeZoom,
} from '../../../shared/utils/chart.utils';
import { loadPageState, savePageState } from '../../../shared/utils/page-state';
import { UrlStateService } from '../../../shared/utils/url-state';
import { downloadCsv, downloadBlob, fileStamp } from '../../../shared/utils/export.utils';

const STATE_KEY = 'state.metricDetail';

/** How the chart folds multiple display series into fewer lines/bucket-sets.
 *  - 'labels': no fold — one line per display series (Gauge/Sum only; distribution types are
 *    always grouped per-service by the server, so this option never applies to them).
 *  - 'service': fold pods of one service into one line (Gauge/Sum) — distribution types are
 *    already at this granularity server-side, so it's a no-op display default for them.
 *  - 'all': collapse every display series (and "other") into a single aggregate line/bucket-set —
 *    phase 6's cross-series Aggregate fold, extended to every metric type. */
type GroupMode = 'labels' | 'service' | 'all';

/** Chart-style tiles (phase 6): alternate renderings of the same `DisplayMetricSeries[]`/
 *  `OtherMetricSeries` the default time-series view renders. Distribution types (histogram/
 *  exp-histogram/summary) only ever get 'timeseries' — a stacked/share/dial view of percentile
 *  lines has no natural composition-of-a-whole meaning. */
type ChartStyle = 'timeseries' | 'stacked' | 'share' | 'dial';

const CHART_STYLE_META: Record<ChartStyle, { icon: string; label: string }> = {
  timeseries: { icon: 'show_chart', label: 'Line' },
  stacked: { icon: 'stacked_line_chart', label: 'Stacked' },
  share: { icon: 'donut_large', label: 'Share' },
  dial: { icon: 'speed', label: 'Dial' },
};

/** ~1 point per 3px of chart width, capped at 1,000 (decision 21 / plan's "Chart-width driven
 *  resolution"). A sane floor keeps a very narrow chart from requesting an unusably coarse series. */
const PX_PER_POINT = 3;
const MIN_POINTS = 50;
const MAX_POINTS = 1000;

const TYPE_LABEL_OF = TYPE_LABELS;

/** One table row on the Exemplars tab: the exemplar plus the data point it was sampled from. */
interface ExemplarRow {
  exemplar: ExemplarModel;
  seriesName: string;
  pointTimestamp: Date;
  /** Preformatted parent value — the point's own value for scalars, its observation count for
   *  distributions, where a single "value" does not exist. */
  pointValue: string;
  /** The exemplar's own measurement, formatted in the metric's unit. */
  value: string;
}

/** One group of points ready to chart: a real display series or the folded "other" bucket. */
interface ChartGroup {
  name: string;
  serviceName: string;
  points: MetricBucketPoint[];
}

/**
 * Line/area charts break the line at every `null`, and the server's bucket grid is typically much
 * finer than the export interval on short windows, so most buckets are empty and a line of real
 * observations renders as isolated, unconnected points. Drop the empty buckets instead so
 * consecutive observations connect (the pre-bucketing behavior). Stacked series must share their
 * x values for the bands to sum, so there an x is kept when any series has data and the others
 * contribute 0 at it.
 */
function connectAcrossEmptyBuckets(
  series: { name: string; data: [number, number | null][] }[],
  stacked: boolean,
): { name: string; data: [number, number | null][] }[] {
  if (!stacked) return series.map((s) => ({ ...s, data: s.data.filter(([, v]) => v != null) }));
  const xs = new Set(series.flatMap((s) => s.data.filter(([, v]) => v != null).map(([x]) => x)));
  return series.map((s) => ({
    ...s,
    data: s.data.filter(([x]) => xs.has(x)).map(([x, v]) => [x, v ?? 0] as [number, number | null]),
  }));
}

@Component({
  selector: 'app-metric-detail',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, KeyValuePipe, SlicePipe, RouterLink, FormsModule,
    MatCardModule, MatButtonModule, MatButtonToggleModule, MatIconModule,
    MatTabsModule, MatTableModule, MatChipsModule, MatFormFieldModule, MatInputModule,
    MatSelectModule, MatProgressBarModule, MatTooltipModule, MatPaginatorModule, MatMenuModule, NgApexchartsModule,
    StatCardComponent, EmptyStateComponent, PageHeaderComponent,
  ],
  templateUrl: './metric-detail.component.html',
  styleUrl: './metric-detail.component.scss',
})
export class MetricDetailComponent implements OnInit, AfterViewInit, OnDestroy {
  @Input() name!: string;

  /** Chart card content area — its pixel width drives the requested point resolution. */
  @ViewChild('chartContainer', { read: ElementRef }) chartContainerRef?: ElementRef<HTMLElement>;

  private readonly api = inject(MetricsApiService);
  private readonly resourcesApi = inject(ResourcesApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly theme = inject(ThemeService);
  private readonly title = inject(Title);
  private readonly urlState = inject(UrlStateService);
  protected readonly tenant = inject(TenantService);
  private readonly capabilitiesService = inject(CapabilitiesService);

  protected capabilities = this.capabilitiesService.capabilities;

  protected loading = signal(true);
  protected metricName = computed(() => decodeURIComponent(this.name));
  protected instances = signal<MetricInfo[]>([]);
  protected labels = signal<Record<string, string[]>>({});
  /** True when the label picker's 1,000-row distinct-set cap was hit; some rare labels may be missing. */
  protected labelsPartial = signal(false);
  protected seriesResult = signal<MetricSeriesResult | null>(null);
  private seriesSub?: Subscription;

  protected selectedService = signal('');
  protected selectedLabels = signal<Record<string, string>>({});
  /** Applied query text (shared `q` grammar, decision 30) — changes only on submit. */
  protected searchText = signal<string>(this.urlState.get('q') ?? '');
  /** Draft text in the search box; applied to `searchText` by `submitSearch()`. */
  protected searchInput = signal<string>(this.searchText());
  /** Cross-series fold (see {@link GroupMode}). */
  protected groupMode = signal<GroupMode>((this.urlState.get('groupBy') as GroupMode) ?? 'labels');
  /** Sum only: raw per-bucket increase vs. a per-second rate (Value / bucket width). */
  protected showRaw = signal(false);
  protected activeTab = signal(0);
  /** Chart-style tile selection (phase 6). Not a data refetch — purely a re-render. */
  protected chartStyle = signal<ChartStyle>((this.urlState.get('chartStyle') as ChartStyle) ?? 'timeseries');
  protected readonly chartStyleMeta = CHART_STYLE_META;

  /** Chart-width-driven point resolution (decision 21). Updated by a ResizeObserver but never
   *  itself triggers a refetch — only read (via `untracked`) the next time a real filter changes,
   *  per the plan's "re-request on window change, not on resize". */
  private chartWidthPx = signal(900);
  private resizeObserver?: ResizeObserver;

  protected metricType = computed(() => this.instances()[0]?.type ?? this.seriesResult()?.type ?? MetricType.Gauge);
  protected metricUnit = computed(() => this.instances()[0]?.unit ?? this.seriesResult()?.unit ?? '');
  /** The unit as display text: empty (so the chip is hidden) for no unit or a braced annotation like `{requests}`. */
  protected unitLabel = computed(() => formatUnitLabel(this.metricUnit()));
  protected typeLabel = computed(() => TYPE_LABEL_OF[this.metricType()] ?? 'Unknown');
  protected typeColor = computed(() => getTypeColor(this.metricType()));

  protected isHistogram = computed(() => this.metricType() === MetricType.Histogram);
  protected isExpHistogram = computed(() => this.metricType() === MetricType.ExponentialHistogram);
  protected isSummary = computed(() => this.metricType() === MetricType.Summary);
  protected isSum = computed(() => this.metricType() === MetricType.Sum);
  protected isDistribution = computed(() => this.isHistogram() || this.isExpHistogram() || this.isSummary());

  /** A gauge whose unit gives the Dial a real 0–100 / 0–1 bound (`%` or the OTLP dimensionless
   *  ratio `1`). Only these get a Dial; unbounded gauges (bytes, temperature, …) have no honest
   *  reference for a radial fill. */
  protected isBoundedGauge = computed(() => {
    if (this.metricType() !== MetricType.Gauge) return false;
    const unit = this.metricUnit();
    return unit === '%' || unit === '1';
  });

  /** Chart-style tiles available for the current metric type (phase 6). Empty for distributions
   *  (selector hidden — a Stacked/Share/Dial rendering of percentile lines has no natural
   *  composition-of-a-whole meaning). Gauges only add a Dial when bounded; Sums get Stacked and
   *  Share composition views. */
  protected chartStyles = computed<ChartStyle[]>(() => {
    if (this.isDistribution()) return [];
    if (this.metricType() === MetricType.Gauge) return this.isBoundedGauge() ? ['timeseries', 'dial'] : ['timeseries'];
    return ['timeseries', 'stacked', 'share'];
  });

  /** Aggregate-fold options for the current metric type/chart-style. Distribution types only ever
   *  offer Per Service (their server-side default granularity) and All — there is no finer,
   *  per-label view to offer since the server already merges those. 'all' is hidden while Stacked
   *  is selected: collapsing every band into one line defeats the point of a stacked chart. */
  protected groupModeOptions = computed<{ value: GroupMode; label: string }[]>(() => {
    const opts: { value: GroupMode; label: string }[] = this.isDistribution()
      ? [{ value: 'service', label: 'Per Service' }]
      : [{ value: 'labels', label: 'Per Series' }, { value: 'service', label: 'Per Service' }];
    if (this.chartStyle() !== 'stacked') opts.push({ value: 'all', label: 'All' });
    return opts;
  });

  /** Every real display series plus the folded "other" bucket, as one flat list for stats/export. */
  protected allGroups = computed<ChartGroup[]>(() => {
    const result = this.seriesResult();
    if (!result) return [];
    const groups: ChartGroup[] = result.series.map((s) => ({ name: s.seriesName, serviceName: s.serviceName, points: s.points }));
    if (result.other && result.other.seriesCount > 0) {
      groups.push({ name: `other (${result.other.seriesCount})`, serviceName: '', points: result.other.points });
    }
    return groups;
  });

  /** {@link allGroups} after applying the current {@link GroupMode} fold. 'stacked' never folds to
   *  'all' (see {@link groupModeOptions}), so that combination is treated as 'service'. */
  protected effectiveGroups = computed<ChartGroup[]>(() => {
    const groups = this.allGroups();
    if (groups.length <= 1) return groups;
    let mode = this.groupMode();
    if (mode === 'all' && this.chartStyle() === 'stacked') mode = 'service';

    if (mode === 'service') {
      const result = this.seriesResult();
      const raw = result?.series.map((s) => ({ name: s.seriesName, serviceName: s.serviceName, points: s.points })) ?? [];
      const other = result?.other && result.other.seriesCount > 0
        ? [{ name: `other (${result.other.seriesCount})`, serviceName: '', points: result.other.points }]
        : [];
      return [...this.foldByService(raw), ...other];
    }
    if (mode === 'all') {
      return [{ name: 'All', serviceName: '', points: foldPointsAcrossGroups(groups, this.metricType()) }];
    }
    return groups;
  });

  /** Throughput (observations/s, the server's elapsed-time-based `rate`) for Histogram/Exp-Histogram/Summary — always
   *  folded across every display series (mirroring the pre-Phase-4 UI, which showed one whole-
   *  metric throughput trend regardless of the per-series/per-service selection above it). */
  protected throughputChartOptions = computed<ApexOptions | null>(() => {
    if (!this.isDistribution()) return null;
    const result = this.seriesResult();
    if (!result) return null;
    const folded = foldPointsAcrossGroups(this.allGroups(), this.metricType());
    const data = folded
      .filter((p) => p.rate != null)
      .map((p) => [new Date(p.timestamp).getTime(), p.rate!] as [number, number]);
    if (!data.length) return null;
    const isDark = this.theme.isDark();
    const { start: rangeStart, end: rangeEnd } = this.timeRange.range();
    return {
      chart: { type: 'area', height: 160, toolbar: { show: false }, background: 'transparent' },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: [{ name: 'Throughput (/s)', data }],
      xaxis: { type: 'datetime', min: rangeStart.getTime(), max: rangeEnd.getTime(), labels: { datetimeUTC: false } },
      stroke: { curve: 'smooth', width: 2 },
      fill: { opacity: 0.15 },
      dataLabels: { enabled: false },
      yaxis: { labels: { formatter: (v: number) => v.toFixed(1) } },
      grid: chartGrid(isDark),
      legend: { show: false },
    };
  });

  /** Bucket-distribution bar (whole-range shape) for Histogram/Exp-Histogram, folded across every
   *  display series — same "always whole-metric" rationale as {@link throughputChartOptions}. */
  protected bucketBarOptions = computed<ApexOptions | null>(() => {
    if (!this.isHistogram() && !this.isExpHistogram()) return null;
    const folded = foldPointsAcrossGroups(this.allGroups(), this.metricType());
    return buildHistogramBarFromBuckets(folded, this.theme.isDark(), this.metricUnit());
  });

  /** Bucket-distribution heatmap (bucket range × time) for Histogram/Exp-Histogram, folded across
   *  every display series — same "always whole-metric" rationale as {@link throughputChartOptions}. */
  protected heatmapOptions = computed<ApexOptions | null>(() => {
    if (!this.isHistogram() && !this.isExpHistogram()) return null;
    const folded = foldPointsAcrossGroups(this.allGroups(), this.metricType());
    return buildHistogramHeatmapFromBuckets(folded, this.theme.isDark(), this.metricUnit());
  });

  protected timedOut = computed(() => this.seriesResult()?.timedOut ?? false);
  protected hasSeriesData = computed(() => this.allGroups().length > 0);
  /** Full failure per decision 31: the retry at a quarter of the requested points also timed out. */
  protected timeoutFailed = computed(() => this.timedOut() && !this.hasSeriesData());
  /** Partial success: the quarter-resolution retry returned something. */
  protected timeoutDegraded = computed(() => this.timedOut() && this.hasSeriesData());

  /** Histogram/exp-histogram streams folded out because their bucket layout didn't match the
   *  layout actually charted (decision 42). */
  protected excludedStreams = computed(() => {
    const result = this.seriesResult();
    if (!result) return 0;
    const fromSeries = result.series.reduce((a, s) => a + (s.excludedStreams ?? 0), 0);
    return fromSeries + (result.other?.excludedStreams ?? 0);
  });

  protected chartOptions = signal<ApexOptions>({});

  /** Latest quantile snapshot per group, for the Summary tab's snapshot table. */
  protected summarySnapshot = computed(() => {
    if (!this.isSummary()) return [];
    const groups = this.allGroups();
    if (!groups.length) return [];
    const rows: { series: string; label: string; value: number }[] = [];
    const multi = groups.length > 1;
    for (const g of groups) {
      const withQ = [...g.points].reverse().find((p) => p.quantiles && p.quantiles.length);
      if (!withQ?.quantiles || !withQ.quantileValues) continue;
      withQ.quantiles.forEach((q, i) => {
        const v = withQ.quantileValues![i];
        if (v == null) return;
        rows.push({ series: g.name, label: `P${(q * 100).toFixed(0)}`, value: v });
      });
    }
    return multi ? rows : rows.map((r) => ({ ...r, series: '' }));
  });

  /** Stats card values, computed directly from the pre-bucketed server points — no client windowing. */
  private stats = computed(() => {
    const empty = { current: null as number | null, min: null as number | null, max: null as number | null, avg: null as number | null, minMaxApproximate: false };
    const groups = this.allGroups();
    if (!groups.length) return empty;
    if (this.isHistogram() || this.isExpHistogram() || this.isSummary()) {
      const allPoints = groups.flatMap((g) => g.points);
      const totalCount = allPoints.reduce((a, p) => a + (p.count ?? 0), 0);
      const totalSum = allPoints.reduce((a, p) => a + (p.sum ?? 0), 0);
      const minsWithFlag = allPoints.filter((p) => p.min != null);
      const maxsWithFlag = allPoints.filter((p) => p.max != null);
      const minPoint = minsWithFlag.length ? minsWithFlag.reduce((a, b) => (a.min! < b.min! ? a : b)) : null;
      const maxPoint = maxsWithFlag.length ? maxsWithFlag.reduce((a, b) => (a.max! > b.max! ? a : b)) : null;
      return {
        current: totalCount > 0 ? totalSum / totalCount : null,
        min: minPoint?.min ?? null,
        max: maxPoint?.max ?? null,
        avg: totalCount,
        // The reported min/max stat is only as trustworthy as the single point it came from.
        minMaxApproximate: !!(minPoint?.minMaxApproximate || maxPoint?.minMaxApproximate),
      };
    }

    // Gauge/Sum: merge every group's points index-wise (all groups share the identical bucket
    // grid the server built — see MetricReadRepositoryBase.MergeBucketsForDisplaySeries).
    // Sum in rate mode reads the server's elapsed-time-based `rate` rather than value / bucket width.
    const rate = this.metricType() === MetricType.Sum && !this.showRaw();
    const present = foldPointsAcrossGroups(groups, this.metricType())
      .map((p) => (rate ? p.rate : p.value))
      .filter((v): v is number => v != null);
    if (!present.length) return empty;

    const allMins = groups.flatMap((g) => g.points.map((p) => p.min)).filter((v): v is number => v != null);
    const allMaxs = groups.flatMap((g) => g.points.map((p) => p.max)).filter((v): v is number => v != null);
    return {
      current: present[present.length - 1],
      min: allMins.length ? Math.min(...allMins) : Math.min(...present),
      max: allMaxs.length ? Math.max(...allMaxs) : Math.max(...present),
      avg: present.reduce((a, b) => a + b, 0) / present.length,
      minMaxApproximate: false, // Gauge/Sum min/max is never a cumulative-histogram estimate.
    };
  });

  protected currentValue = computed(() => this.stats().current);
  protected minValue = computed(() => this.stats().min);
  protected maxValue = computed(() => this.stats().max);
  protected avgValue = computed(() => this.stats().avg);
  /** True when the Min/Max stat cards are a bound estimate — see MetricBucketPoint.minMaxApproximate. */
  protected minMaxApproximate = computed(() => this.stats().minMaxApproximate);

  /** Stats are rates only for Sum when not showing raw per-bucket increases. */
  protected statsAreRates = computed(() => this.isSum() && !this.showRaw());
  protected services = signal<string[]>([]);
  protected labelKeys = computed(() => Object.keys(this.labels()));

  /** Metadata rows for the Metadata tab. Per-point metadata (flags, temporality, exponential-
   *  histogram scale/offsets) is no longer available — the server returns pre-aggregated buckets,
   *  not raw points, so those per-point fields have nothing to summarize. */
  protected metadataRows = computed(() => {
    const info = this.instances()[0];
    const result = this.seriesResult();
    const rows: { label: string; value: string }[] = [];
    rows.push({ label: 'Metric Name', value: this.metricName() });
    rows.push({ label: 'Type', value: this.typeLabel() });
    const unitLabel = formatUnitLabel(info?.unit);
    if (unitLabel) rows.push({ label: 'Unit', value: unitLabel });
    if (info?.description) rows.push({ label: 'Description', value: info.description });
    rows.push({ label: 'Services', value: this.services().join(', ') || '—' });
    rows.push({ label: 'Instance Count', value: String(this.instances().length) });
    if (info) {
      rows.push({ label: 'First Seen', value: new Date(info.firstSeen).toLocaleString() });
      rows.push({ label: 'Last Seen', value: new Date(info.lastSeen).toLocaleString() });
      rows.push({ label: 'Data Point Count', value: String(info.dataPointCount) });
    }
    if (result) {
      rows.push({ label: 'Bucket Width', value: `${(result.bucketWidthMs / 1000).toFixed(1)}s` });
      rows.push({ label: 'Series Returned', value: String(result.series.length + (result.other ? 1 : 0)) });
    }
    return rows;
  });

  /**
   * Exemplars — tier-aware (decision 26). Analytics tier: real server keyset paging, mirroring the
   * Logs/Traces paginator pattern. Standard tier: the newest-500 capped response, paged client-side.
   */
  protected exemplarsPage = signal<MetricExemplarPage | null>(null);
  protected exemplarsLoading = signal(false);
  protected exemplarsLoaded = signal(false);
  private exemplarSub?: Subscription;
  private static readonly EXEMPLARS_TAB = 3;

  protected isAnalyticsExemplars = computed(() => this.capabilities().exemplarPaging);

  /** Tracked client-side (decision 1: keyset paging has no server page number); also used as the
   *  standard-tier client-paginator index over the capped newest-500 list. */
  protected exemplarPageIndex = signal(0);
  protected exemplarPageSize = signal(25);
  protected readonly exemplarPageSizeOptions = [25, 50, 100];

  private toExemplarRow(e: MetricExemplar): ExemplarRow {
    const measured = e.exemplar.valueDouble ?? e.exemplar.valueInt;
    const unit = this.metricUnit();
    return {
      exemplar: e.exemplar,
      seriesName: e.seriesName,
      pointTimestamp: new Date(e.pointTimestamp),
      pointValue: this.isDistribution()
        ? `${e.pointCount ?? 0} obs`
        : formatUnitValue(e.pointDoubleValue ?? e.pointIntValue ?? 0, unit),
      value: measured == null ? '—' : formatUnitValue(measured, unit),
    };
  }

  /** Rows currently on screen: server-paged on the analytics tier, client-sliced on the standard tier. */
  protected pagedExemplars = computed<ExemplarRow[]>(() => {
    const page = this.exemplarsPage();
    if (!page) return [];
    const rows = page.exemplars.map((e) => this.toExemplarRow(e));
    if (this.isAnalyticsExemplars()) return rows;
    const start = this.exemplarPageIndex() * this.exemplarPageSize();
    return rows.slice(start, start + this.exemplarPageSize());
  });

  /** Paginator length: the server's own total on the analytics tier (a lower bound if it timed
   *  out under the summary timeout), the capped list length on the standard tier. */
  protected exemplarsTotal = computed(() => {
    const page = this.exemplarsPage();
    if (!page) return 0;
    if (this.isAnalyticsExemplars()) return page.total ?? page.exemplars.length;
    return page.exemplars.length;
  });
  protected exemplarsTotalIsLowerBound = computed(() => this.isAnalyticsExemplars() && (this.exemplarsPage()?.totalIsLowerBound ?? false));
  protected exemplarsCapped = computed(() => !this.isAnalyticsExemplars() && (this.exemplarsPage()?.capped ?? false));

  protected exemplarsTabLabel = computed(() => {
    if (!this.exemplarsLoaded()) return 'Exemplars';
    const n = this.exemplarsTotal();
    return this.exemplarsTotalIsLowerBound() ? `Exemplars (${n}+)` : `Exemplars (${n})`;
  });

  protected readonly exemplarColumns = ['time', 'value', 'series', 'point', 'traceId', 'spanId', 'attrs'];

  private currentFilter() {
    const { start, end } = this.timeRange.range();
    const name = this.metricName();
    const svc = this.selectedService();
    const metricId = svc ? this.instances().find((i) => i.serviceName === svc)?.id : undefined;
    const labelFilters = Object.keys(this.selectedLabels()).length > 0 ? this.selectedLabels() : undefined;
    const q = this.searchText().trim() || undefined;
    return { name, start, end, svc, metricId, labelFilters, q };
  }

  private loadExemplars(nav: 'first' | 'next' | 'prev' | 'last' = 'first'): void {
    const { name, start, end, svc, metricId, labelFilters, q } = this.currentFilter();

    if (svc && metricId === undefined) {
      this.exemplarsPage.set({ name, type: this.metricType(), exemplars: [], totalIsLowerBound: false, capped: false });
      this.exemplarsLoaded.set(true);
      return;
    }

    const current = this.exemplarsPage();
    const cursor = nav === 'next' ? current?.nextCursor ?? undefined
      : nav === 'prev' ? current?.prevCursor ?? undefined
      : undefined;

    this.exemplarsLoading.set(true);
    this.exemplarSub?.unsubscribe();
    this.exemplarSub = this.api.getExemplars({
      metricName: name, start, end, metricId, labelFilters, q,
      size: this.exemplarPageSize(), cursor, nav,
    }).subscribe({
      next: (page) => {
        this.exemplarsPage.set(page);
        this.exemplarsLoaded.set(true);
        this.exemplarsLoading.set(false);
      },
      error: () => this.exemplarsLoading.set(false),
    });
  }

  /** OTLP timestamps are nanoseconds since the epoch; JS dates are milliseconds. */
  protected nanoToDate(nano: number): Date {
    return new Date(nano / 1_000_000);
  }

  constructor() {
    // Slide relative preset windows to "now" on (re)entry so navigating back refreshes.
    this.timeRange.refreshRelativeWindow();

    // Tenant-wide, signal-agnostic — fetched once, not derived from this metric's instances.
    this.resourcesApi.getServices().subscribe({
      next: (services) => this.services.set(services),
    });

    effect(() => {
      this.timeRange.range();
      this.selectedService();
      this.selectedLabels();
      this.searchText();
      untracked(() => this.loadAll());
    });

    // Rebuild the chart when the theme toggles or the group mode/raw-rate toggle changes so colors
    // and folding track the UI without a refetch.
    effect(() => {
      this.theme.isDark();
      this.groupMode();
      this.showRaw();
      this.chartStyle();
      untracked(() => {
        if (this.seriesResult()) this.buildChart();
      });
    });

    // Load exemplars lazily, only once the Exemplars tab is actually opened — including the
    // restored-page-state case where a user's last visit ended on that tab (activeTab is persisted).
    // Also reload whenever the active filter changes while the tab is already open.
    effect(() => {
      const tab = this.activeTab();
      this.timeRange.range();
      this.selectedService();
      this.selectedLabels();
      this.searchText();
      untracked(() => {
        if (tab === MetricDetailComponent.EXEMPLARS_TAB) {
          this.exemplarPageIndex.set(0);
          this.loadExemplars('first');
        } else {
          this.exemplarsLoaded.set(false);
        }
      });
    });

    // Mirror filter state into the URL (shareable/deep-linkable). `points` is derived from chart
    // width, never persisted (plan's explicit URL-state list).
    effect(() => {
      this.urlState.patch({
        q: this.searchText() || null,
        groupBy: this.groupMode() !== 'labels' ? this.groupMode() : null,
        chartStyle: this.chartStyle() !== 'timeseries' ? this.chartStyle() : null,
      });
    });

    // Persist filter/view/tab state (scoped to the current metric).
    effect(() => {
      savePageState(STATE_KEY, {
        metricName: this.metricName(),
        selectedService: this.selectedService(),
        selectedLabels: this.selectedLabels(),
        groupMode: this.groupMode(),
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
      groupMode: 'labels' as GroupMode, showRaw: false, activeTab: 0, chartStyle: 'timeseries' as ChartStyle,
    });
    if (saved.metricName === this.metricName()) {
      this.selectedService.set(saved.selectedService);
      this.selectedLabels.set(saved.selectedLabels);
      this.groupMode.set(saved.groupMode === 'service' || saved.groupMode === 'all' ? saved.groupMode : 'labels');
      this.showRaw.set(saved.showRaw);
      this.activeTab.set(saved.activeTab);
      this.chartStyle.set(['stacked', 'share', 'dial'].includes(saved.chartStyle) ? saved.chartStyle : 'timeseries');
    }
  }

  ngAfterViewInit(): void {
    const el = this.chartContainerRef?.nativeElement;
    if (!el || typeof ResizeObserver === 'undefined') return;
    this.resizeObserver = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect?.width;
      if (w && w > 0) this.chartWidthPx.set(Math.round(w));
    });
    this.resizeObserver.observe(el);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.seriesSub?.unsubscribe();
    this.exemplarSub?.unsubscribe();
  }

  /** Requested point count for the current chart width (decision 21), clamped to [MIN_POINTS, MAX_POINTS]. */
  private requestedPoints(): number {
    const px = untracked(() => this.chartWidthPx());
    return Math.min(MAX_POINTS, Math.max(MIN_POINTS, Math.round(px / PX_PER_POINT)));
  }

  private loadAll(): void {
    this.loading.set(true);
    const { start, end } = this.timeRange.range();
    const name = this.metricName();

    forkJoin({
      instances: this.api.getByName(name),
      labels: this.api.getLabels(name, start, end),
    }).subscribe({
      next: ({ instances, labels }) => {
        this.instances.set(instances);
        this.labels.set(labels.labels);
        this.labelsPartial.set(labels.partial);
        this.reloadSeries();
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected reloadSeries(): void {
    const { name, start, end, svc, metricId } = this.currentFilter();
    const labelFilters = Object.keys(this.selectedLabels()).length > 0 ? this.selectedLabels() : undefined;
    const q = this.searchText().trim() || undefined;

    // A service can be selected (from the tenant-wide list) with no instance of this particular
    // metric. Rather than querying with metricId=undefined — which means "no filter" and would
    // silently show every service's data — render the same "no data" empty state as a genuinely
    // empty range.
    if (svc && metricId === undefined) {
      this.seriesResult.set({ name, type: this.metricType(), bucketWidthMs: 0, timedOut: false, series: [] });
      this.buildChart();
      return;
    }

    this.seriesSub?.unsubscribe();
    this.seriesSub = this.api.getSeries({
      metricName: name, start, end, metricId, labelFilters, q, points: this.requestedPoints(), top: 8,
    }).subscribe((result) => {
      this.seriesResult.set(result);
      this.buildChart();
    });
  }

  /** Wheel-zoom off + drag-select drives the shared time-range picker (datetime charts). */
  private zoomChart() {
    return timeRangeZoom((start, end) => this.timeRange.setCustom(start, end));
  }

  /** Folds groups sharing a service name into one line, index-wise — safe because every group
   *  shares the identical server-built bucket grid (same start/width/count). Gauge/Sum only. */
  private foldByService(groups: ChartGroup[]): ChartGroup[] {
    const byService = new Map<string, ChartGroup[]>();
    for (const g of groups) {
      const key = g.serviceName || g.name;
      const list = byService.get(key) ?? [];
      list.push(g);
      byService.set(key, list);
    }
    const isGauge = this.metricType() === MetricType.Gauge;
    return [...byService.entries()].map(([svc, list]) => {
      const len = Math.max(...list.map((g) => g.points.length), 0);
      const points: MetricBucketPoint[] = [];
      for (let i = 0; i < len; i++) {
        const vals = list.map((g) => g.points[i]?.value).filter((v): v is number => v != null);
        const ts = list.find((g) => g.points[i])?.points[i]?.timestamp ?? '';
        points.push({
          timestamp: ts,
          value: vals.length ? (isGauge ? vals.reduce((a, b) => a + b, 0) / vals.length : vals.reduce((a, b) => a + b, 0)) : undefined,
          rate: isGauge ? undefined : sumDefined(list.map((g) => g.points[i]?.rate)),
        });
      }
      return { name: svc, serviceName: svc, points };
    });
  }

  private buildChart(): void {
    const result = this.seriesResult();
    if (!result) { this.chartOptions.set({}); return; }
    // Guard against a chart style that no longer applies (e.g. restored from a saved/URL state for
    // a different metric, or the metric's own type/unit resolved after the initial guess).
    if (!this.chartStyles().includes(this.chartStyle())) this.chartStyle.set('timeseries');

    const isDark = this.theme.isDark();

    if (this.chartStyle() === 'share') { this.buildShareChart(isDark); return; }
    if (this.chartStyle() === 'dial') { this.buildDialChart(isDark); return; }

    const { start: rangeStart, end: rangeEnd } = this.timeRange.range();
    const stacked = this.chartStyle() === 'stacked';

    let chartSeries: { name: string; data: [number, number | null][] }[];
    let chartType: 'area' | 'bar' | 'line' = 'area';

    if (this.isDistribution()) {
      chartType = 'line';
      chartSeries = this.buildDistributionSeries();
    } else {
      const rate = this.isSum() && !this.showRaw();
      const groups = this.effectiveGroups();
      chartType = stacked ? (rate ? 'area' : 'bar') : (this.isSum() && this.showRaw() ? 'bar' : 'area');
      chartSeries = groups.map((g) => ({
        name: g.name,
        data: g.points.map((p) => [new Date(p.timestamp).getTime(), (rate ? p.rate : p.value) ?? null] as [number, number | null]),
      }));
    }

    if (chartType !== 'bar') chartSeries = connectAcrossEmptyBuckets(chartSeries, stacked);

    // Every type formats through its unit. A Sum shown as a rate is `unit/s`; a raw per-bucket
    // increase (and a Gauge or distribution) is in the metric's own unit.
    const unit = this.metricUnit();
    const perSecond = this.isSum() && !this.showRaw();
    const valueFormatter = (v: number) => formatUnitValue(v, unit, { perSecond });

    // With empty buckets dropped, only a series holding a single point draws no line — mark
    // those so a lone observation is still visible; every other series stays a plain line.
    const markerSizes = chartSeries.map((s) => (chartType !== 'bar' && s.data.length === 1 ? 4 : 0));

    this.chartOptions.set({
      chart: { type: chartType, height: 300, stacked, toolbar: { show: false }, background: 'transparent', ...this.zoomChart() },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: chartSeries,
      xaxis: { type: 'datetime', min: rangeStart.getTime(), max: rangeEnd.getTime(), labels: { datetimeUTC: false } },
      stroke: { curve: 'smooth', width: stacked && chartType === 'area' ? 1 : 2 },
      markers: { size: markerSizes, strokeWidth: 0, hover: { sizeOffset: 2 } },
      fill: { opacity: chartType === 'area' ? (stacked ? 0.7 : 0.15) : 1 },
      dataLabels: { enabled: false },
      yaxis: { labels: { formatter: valueFormatter } },
      tooltip: { y: { formatter: valueFormatter } },
      grid: chartGrid(isDark),
      legend: { position: 'top' },
    });
  }

  /** Share/donut view (phase 6): composition-of-total across display series, for Sum metrics only
   *  (see {@link chartStyles}). Each slice is that series' total increase over the visible window
   *  — the raw per-bucket sum, independent of the Rate/Raw toggle, since "share of total" is
   *  meaningless for a rate. */
  private buildShareChart(isDark: boolean): void {
    const groups = this.allGroups();
    const slices = groups.map((g) => ({ name: g.name, value: g.points.reduce((a, p) => a + (p.value ?? 0), 0) }));
    this.chartOptions.set(buildShareDonut(slices, isDark, this.metricUnit()) ?? {});
  }

  /** Dial/radial-gauge view (phase 6): current value against the unit's natural bound (100 for
   *  `%`, 1 for the dimensionless ratio `1`) — see {@link isBoundedGauge}. */
  private buildDialChart(isDark: boolean): void {
    const value = this.currentValue() ?? 0;
    const unit = this.metricUnit();
    const max = unit === '1' ? 1 : 100;
    this.chartOptions.set(buildRadialGauge(value, max, this.metricName(), isDark, unit === '1' ? '' : unit));
  }

  /** Histogram/exp-histogram: p50/p95/p99 + Max per group, from the server's already-merged bucket
   *  counts. Summary: one line per quantile fraction present, per group. Groups are prefixed with
   *  their name only when more than one contributes (kept plain otherwise, matching prior UX). */
  private buildDistributionSeries(): { name: string; data: [number, number | null][] }[] {
    const groups = this.effectiveGroups();
    const multi = groups.length > 1;
    const out: { name: string; data: [number, number | null][] }[] = [];

    if (this.isSummary()) {
      for (const g of groups) {
        const fracs = new Set<number>();
        for (const p of g.points) for (const q of p.quantiles ?? []) fracs.add(q);
        const sorted = [...fracs].sort((a, b) => a - b);
        for (const q of sorted) {
          const label = `P${(q * 100).toFixed(0)}`;
          out.push({
            name: multi ? `${g.name} ${label}` : label,
            data: g.points.map((p) => {
              const idx = p.quantiles?.findIndex((x) => Math.abs(x - q) < 1e-7) ?? -1;
              const v = idx >= 0 ? p.quantileValues?.[idx] ?? null : null;
              return [new Date(p.timestamp).getTime(), v] as [number, number | null];
            }),
          });
        }
      }
      return out;
    }

    const percentiles: { q: number; label: string }[] = [{ q: 0.5, label: 'p50' }, { q: 0.95, label: 'p95' }, { q: 0.99, label: 'p99' }];
    for (const g of groups) {
      for (const { q, label } of percentiles) {
        out.push({
          name: multi ? `${g.name} ${label}` : label,
          data: g.points.map((p) => {
            if (!p.bucketCounts || !p.count) return [new Date(p.timestamp).getTime(), null];
            const v = histogramQuantile(p.bucketCounts, p.bucketBounds ?? [], q);
            return [new Date(p.timestamp).getTime(), Number.isFinite(v) ? v : null] as [number, number | null];
          }),
        });
      }
      // "Max (≈)" whenever any charted point's max is an estimate, not this bucket's true extreme
      // (cumulative histogram, unchanged lifetime max — see MetricBucketPoint.minMaxApproximate).
      const maxLabel = g.points.some((p) => p.minMaxApproximate) ? 'Max (≈)' : 'Max';
      out.push({
        name: multi ? `${g.name} ${maxLabel}` : maxLabel,
        data: g.points.map((p) => [new Date(p.timestamp).getTime(), p.max ?? null] as [number, number | null]),
      });
    }
    return out;
  }

  /** Count card only: a whole-number observation count, thousands-separated — never decimals. */
  protected fmt(v: number | null | undefined): string {
    return v != null ? Math.round(v).toLocaleString() : '—';
  }

  protected fmtStat(v: number | null | undefined, approximate = false): string {
    if (v == null) return '—';
    const prefix = approximate ? '≈ ' : '';
    return prefix + formatUnitValue(v, this.metricUnit(), { perSecond: this.statsAreRates() });
  }

  /** Tooltip text for the Min/Max stat cards when {@link minMaxApproximate} is true. */
  protected readonly minMaxApproximateTooltip =
    'Estimated: this metric reports cumulative min/max since it started, not per interval, so the ' +
    'exact value within this time range can\'t always be recovered. This is a bound, not the true extreme.';

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
  }

  protected onSearchChange(value: string): void {
    this.searchInput.set(value);
    this.searchText.set(value);
  }
  protected submitSearch(): void { this.onSearchChange(this.searchInput().trim()); }

  protected onGroupModeChange(mode: GroupMode): void {
    this.groupMode.set(mode);
  }

  protected onChartStyleChange(style: ChartStyle): void {
    this.chartStyle.set(style);
  }

  /**
   * Maps MatPaginator's page event onto a keyset `nav` on the analytics tier (decision 1: no
   * arbitrary page jump); a plain client-side slice on the standard tier (already-capped list).
   */
  protected onExemplarsPage(e: PageEvent): void {
    if (!this.isAnalyticsExemplars()) {
      this.exemplarPageIndex.set(e.pageIndex);
      this.exemplarPageSize.set(e.pageSize);
      return;
    }

    if (e.pageSize !== this.exemplarPageSize()) {
      this.exemplarPageSize.set(e.pageSize);
      this.exemplarPageIndex.set(0);
      this.loadExemplars('first');
      return;
    }

    const lastIndex = Math.max(0, Math.ceil(this.exemplarsTotal() / this.exemplarPageSize()) - 1);
    let nav: 'first' | 'next' | 'prev' | 'last';
    if (e.pageIndex === 0) nav = 'first';
    else if (!this.exemplarsTotalIsLowerBound() && e.pageIndex >= lastIndex) nav = 'last';
    else if (e.pageIndex > (e.previousPageIndex ?? 0)) nav = 'next';
    else nav = 'prev';

    this.exemplarPageIndex.set(e.pageIndex);
    this.loadExemplars(nav);
  }

  /** Build and download a per-type CSV of the currently charted bucket points. */
  protected exportCsv(): void {
    const groups = this.allGroups();
    if (!groups.length) return;
    const type = this.metricType();

    const ser = (v: unknown): string => (v == null ? '' : JSON.stringify(v));
    const ts = (p: MetricBucketPoint) => new Date(p.timestamp).toLocaleString();

    let headers: string[];
    let row: (series: string, p: MetricBucketPoint) => (string | number)[];

    switch (type) {
      case MetricType.Histogram:
      case MetricType.ExponentialHistogram:
        headers = ['Timestamp', 'Series', 'Count', 'Sum', 'Min', 'Max', 'BucketCounts', 'BucketBounds', 'Rate'];
        row = (s, p) => [ts(p), s, p.count ?? '', p.sum ?? '', p.min ?? '', p.max ?? '', ser(p.bucketCounts), ser(p.bucketBounds), p.rate ?? ''];
        break;
      case MetricType.Summary:
        headers = ['Timestamp', 'Series', 'Count', 'Sum', 'Quantiles', 'QuantileValues', 'IsApproximate', 'Rate'];
        row = (s, p) => [ts(p), s, p.count ?? '', p.sum ?? '', ser(p.quantiles), ser(p.quantileValues), String(p.isApproximate ?? false), p.rate ?? ''];
        break;
      default: // Gauge / Sum
        headers = ['Timestamp', 'Series', 'Value', 'Min', 'Max', 'Rate'];
        row = (s, p) => [ts(p), s, p.value ?? '', p.min ?? '', p.max ?? '', p.rate ?? ''];
    }

    const data = groups.flatMap((g) => g.points.map((p) => row(g.name, p)));
    downloadCsv(`${this.metricName()}_${fileStamp()}.csv`, headers, data);
  }

  /** Tracks whether a server export is in flight, so the menu can disable itself against a double-click. */
  protected readonly serverExportPending = signal(false);

  /**
   * Server-side streaming export (list-pages-server-side plan, Phase 8, decision 29): one row per
   * (display series, bucket), EVERY series included — unlike {@link exportCsv}'s chart-based export,
   * which reflects the chart's own top-8-plus-"other" fold. Builds the request the same way
   * {@link reloadSeries} does (current metric/window/label filters/search), but with no `top` — the
   * export endpoint never folds series into "other".
   */
  protected exportServerSide(format: 'ndjson' | 'csv'): void {
    if (this.serverExportPending()) return;
    const { name, start, end, metricId } = this.currentFilter();
    const labelFilters = Object.keys(this.selectedLabels()).length > 0 ? this.selectedLabels() : undefined;
    const q = this.searchText().trim() || undefined;

    this.serverExportPending.set(true);
    this.api.getSeriesExport({ metricName: name, start, end, metricId, labelFilters, q, points: this.requestedPoints() }, format).subscribe({
      next: (blob) => downloadBlob(`${this.metricName()}-export_${fileStamp()}.${format}`, blob),
      error: () => this.serverExportPending.set(false),
      complete: () => this.serverExportPending.set(false),
    });
  }
}
