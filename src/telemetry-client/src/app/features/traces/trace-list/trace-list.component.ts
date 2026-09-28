import { Component, NgZone, computed, effect, inject, signal, untracked, OnDestroy } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { Router } from '@angular/router';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSort, MatSortModule, Sort } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatChipsModule } from '@angular/material/chips';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { FormsModule } from '@angular/forms';
import { NgxGraphModule } from '@swimlane/ngx-graph';
import { NgApexchartsModule } from 'ng-apexcharts';
import { Subscription } from 'rxjs';
import type { ApexOptions } from 'ng-apexcharts';

import { TracesApiService, TraceLatencyBucket, TraceSummaryResult, TracePageResult } from '../../../core/services/api/traces-api.service';
import { ResourcesApiService } from '../../../core/services/api/resources-api.service';
import { TimeRangeService } from '../../../core/services/time-range.service';
import { ThemeService } from '../../../core/services/theme.service';
import { CapabilitiesService } from '../../../core/services/capabilities.service';
import { TraceInfo, ServiceDependency, OperationStats } from '../../../core/models/trace.models';
import { StatCardComponent } from '../../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { formatDuration, parseDotnetTimespan, timeRangeZoom } from '../../../shared/utils/chart.utils';
import { parseSearchQuery, ParsedSearchQuery } from '../../../shared/utils/search-query.parser';
import { TraceSearchHelpDialogComponent } from '../trace-search-help-dialog/trace-search-help-dialog.component';
import { loadPageState, savePageState } from '../../../shared/utils/page-state';
import { UrlStateService } from '../../../shared/utils/url-state';
import { serviceColor } from '../../../shared/utils/service-colors';
import { downloadCsv, downloadJson, downloadBlob, copyPermalink, fileStamp } from '../../../shared/utils/export.utils';

interface GraphNode {
  id: string; label: string;
  /** Aggregate error rate (0–1) of calls involving this service, for health coloring. */
  errorRate: number;
  callCount: number;
  color: string;
}
interface GraphLink {
  id: string; source: string; target: string; label: string;
  callCount: number; errorRate: number; avgDurationMs: number;
  color: string; width: number;
}

type FilterMode = 'all' | 'errors' | 'slow';
type SortDir = '' | 'asc' | 'desc';
type ChartView = 'volume' | 'latency';

const BUCKET_COUNT = 60;
const STATE_KEY = 'state.traces';
/** How often the "new since" banner's summary re-poll runs, while the tab is visible (same cadence as logs). */
const NEW_SINCE_POLL_MS = 30_000;
/** Explains the "request traces" population behind the chart and stat cards (decision 13). */
const REQUEST_TRACES_TOOLTIP =
  'Traces whose anchor span is an incoming request (SERVER or CONSUMER kind). Background and client-rooted traces are excluded.';
/** Discrete error-ratio tiers for latency-bubble coloring (ApexCharts colors per series, not per point). */
const ERROR_TIERS: { max: number; color: string; label: string }[] = [
  { max: 0, color: '#2196f3', label: 'OK' },
  { max: 0.25, color: '#ffb300', label: 'Low errors' },
  { max: 0.75, color: '#fb8c00', label: 'Mixed' },
  { max: 1, color: '#e53935', label: 'High errors' },
];

@Component({
  selector: 'app-trace-list',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, FormsModule,
    MatCardModule, MatPaginatorModule, MatTableModule, MatSortModule, MatTabsModule, MatIconModule,
    MatButtonToggleModule, MatButtonModule, MatSelectModule, MatFormFieldModule,
    MatInputModule, MatProgressBarModule, MatChipsModule, MatTooltipModule,
    MatDialogModule, MatMenuModule, NgxGraphModule, NgApexchartsModule,
    StatCardComponent, EmptyStateComponent, PageHeaderComponent,
  ],
  templateUrl: './trace-list.component.html',
  styleUrl: './trace-list.component.scss',
})
export class TraceListComponent implements OnDestroy {
  private readonly api = inject(TracesApiService);
  private readonly resourcesApi = inject(ResourcesApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly theme = inject(ThemeService);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly urlState = inject(UrlStateService);
  private readonly zone = inject(NgZone);
  private readonly capabilitiesService = inject(CapabilitiesService);

  private readonly saved = loadPageState(STATE_KEY, {
    filterMode: 'all' as FilterMode,
    selectedService: '',
    selectedOperation: '',
    searchText: '',
    minDurationMs: 500,
    maxDurationMs: 0,
    pageSize: 100,
    chartView: 'volume' as ChartView,
  });

  protected summaryLoading = signal(true);
  protected pageLoading = signal(true);
  protected summary = signal<TraceSummaryResult | null>(null);
  protected page = signal<TracePageResult | null>(null);
  private pageSub?: Subscription;
  private summarySub?: Subscription;
  private newSincePollHandle?: ReturnType<typeof setInterval>;

  protected capabilities = this.capabilitiesService.capabilities;

  protected services = signal<string[]>([]);
  protected dependencies = signal<ServiceDependency[]>([]);
  /** Operations for the selected service, populating the operation dropdown. */
  protected operations = signal<string[]>([]);
  /** Per-operation RED metrics for the Analytics tab. */
  protected operationStats = signal<OperationStats[]>([]);

  protected filterMode = signal<FilterMode>((this.urlState.get('mode') as FilterMode) ?? this.saved.filterMode);
  protected selectedService = signal<string>(this.urlState.get('service') ?? this.saved.selectedService);
  protected selectedOperation = signal<string>(this.urlState.get('op') ?? this.saved.selectedOperation);
  /** Applied query — what filtering, the URL and saved state read. Changes only on submit. */
  protected searchText = signal<string>(this.urlState.get('q') ?? this.saved.searchText);
  /** Draft text in the search box; applied to `searchText` by `submitSearch()`. */
  protected searchInput = signal<string>(this.searchText());
  protected minDurationMs = signal<number>(this.readNum('minDur') ?? this.saved.minDurationMs);
  protected maxDurationMs = signal<number>(this.readNum('maxDur') ?? this.saved.maxDurationMs);
  protected analyticsService = signal('');
  protected analyticsSort = signal<{ col: string; dir: SortDir }>({ col: 'count', dir: 'desc' });

  protected chartView = signal<ChartView>((this.urlState.get('chart') as ChartView) ?? this.saved.chartView);

  /** Selected tab index (Traces / Service Map / Analytics); driven by service-map node clicks. */
  protected selectedTab = signal(0);

  /** Transient "Link copied!" affordance for the copy-permalink button. */
  protected linkCopied = signal(false);

  /** Tracked client-side (decision 1: keyset paging has no server-side page number). Reset on any filter change. */
  protected pageIndex = signal(0);
  protected pageSize = signal(this.readNum('size') ?? this.saved.pageSize);
  protected readonly pageSizeOptions = [100, 250, 500];

  protected parsedQuery = computed<ParsedSearchQuery>(() => parseSearchQuery(this.searchText()));
  protected isTraceIdSearch = computed(() => this.parsedQuery().isTraceIdSearch);

  /** The whole raw search text goes to the server now (decision 10: parsed server-side). */
  private serverQuery = computed(() => this.searchText().trim());

  protected displayRows = computed<TraceInfo[]>(() => this.page()?.items ?? []);
  protected loading = computed(() => this.summaryLoading());

  protected effectiveTotal = computed(() => this.summary()?.listTotal ?? 0);
  protected totalIsLowerBound = computed(() => this.summary()?.totalIsLowerBound ?? false);

  /** "N new since …" banner. */
  protected newSinceCount = computed(() => this.summary()?.newSinceAsOf ?? 0);

  /**
   * Standard-tier search window limit, explained inline next to the search box (decision 39):
   * shown when a raw search filter or `mode=slow` is present and the window exceeds the limit.
   */
  protected rawSearchWindowMessage = computed<string | null>(() => {
    const caps = this.capabilities();
    if (caps.rawSearchWindowHours == null) return null;
    const hasFilter = this.parsedQuery().terms.length > 0 || this.filterMode() === 'slow';
    if (!hasFilter) return null;
    const { start, end } = this.timeRange.range();
    const hours = (end.getTime() - start.getTime()) / 3_600_000;
    if (hours <= caps.rawSearchWindowHours) return null;
    return `Search is limited to a ${caps.rawSearchWindowHours}-hour window on ${caps.tier} tier. Narrow the time range or remove the search/slow filter.`;
  });

  protected errorCount = computed(() => this.summary()?.summary.errorCount ?? 0);
  protected errorRate = computed(() => {
    const s = this.summary()?.summary;
    if (!s || s.count === 0) return '0%';
    return ((s.errorCount / s.count) * 100).toFixed(1) + '%';
  });
  protected avgDuration = computed(() => {
    const s = this.summary()?.summary;
    if (!s || s.count === 0) return '—';
    // p50 is the closest thing the summary carries to a single "typical" duration; the former
    // avg-from-buckets figure isn't available since the summary no longer ships a raw duration
    // sum at the window level outside the per-bucket buckets.
    return formatDuration(s.p50Ms);
  });
  protected readonly requestTracesTooltip = REQUEST_TRACES_TOOLTIP;

  /** Per-service health (error rate + call volume), derived from the dependency edges. */
  private nodeHealth = computed(() => {
    const deps = this.dependencies();
    const map = new Map<string, { errorRate: number; callCount: number }>();
    const services = new Set<string>([
      ...deps.map((d) => d.parentService),
      ...deps.map((d) => d.childService),
    ]);
    for (const svc of services) {
      let edges = deps.filter((d) => d.childService === svc);
      if (edges.length === 0) edges = deps.filter((d) => d.parentService === svc);
      const callCount = edges.reduce((a, d) => a + d.callCount, 0);
      const errors = edges.reduce((a, d) => a + d.errorCount, 0);
      map.set(svc, { errorRate: callCount > 0 ? errors / callCount : 0, callCount });
    }
    return map;
  });

  protected graphNodes = computed<GraphNode[]>(() => {
    const health = this.nodeHealth();
    return [...new Set([
      ...this.dependencies().map((d) => d.parentService),
      ...this.dependencies().map((d) => d.childService),
    ])].map((s) => {
      const h = health.get(s);
      const errorRate = h?.errorRate ?? 0;
      return { id: s, label: s, errorRate, callCount: h?.callCount ?? 0, color: this.nodeColor(errorRate) };
    });
  });

  protected graphLinks = computed<GraphLink[]>(() => {
    const deps = this.dependencies();
    const maxCalls = Math.max(1, ...deps.map((d) => d.callCount));
    return deps.map((d, i) => ({
      id: `link-${i}`,
      source: d.parentService,
      target: d.childService,
      label: formatDuration(d.avgDurationMs),
      callCount: d.callCount,
      errorRate: d.errorRate,
      avgDurationMs: d.avgDurationMs,
      color: this.edgeColor(d.errorRate),
      width: 1.5 + (d.callCount / maxCalls) * 4.5,
    }));
  });

  /** RED-metrics rows for the Analytics tab, sorted by the clicked column (default: calls desc). Small, already-aggregated set — client-side sort stays (decision 4 is about the paged trace list, not this). */
  protected operationRows = computed<OperationStats[]>(() => {
    const rows = [...this.operationStats()];
    const { col, dir } = this.analyticsSort();
    if (!dir) return rows;
    const sign = dir === 'asc' ? 1 : -1;
    const key = (r: OperationStats): number | string => {
      switch (col) {
        case 'operation': return r.operation;
        case 'rate': return r.ratePerSecond;
        case 'errorRate': return r.errorRate;
        case 'p50': return r.p50Ms;
        case 'p95': return r.p95Ms;
        case 'p99': return r.p99Ms;
        case 'avg': return r.avgMs;
        default: return r.count;
      }
    };
    return rows.sort((a, b) => {
      const ka = key(a), kb = key(b);
      return typeof ka === 'string' || typeof kb === 'string'
        ? sign * String(ka).localeCompare(String(kb))
        : sign * (ka - kb);
    });
  });

  protected readonly analyticsColumns = ['operation', 'count', 'rate', 'errorRate', 'p50', 'p95', 'p99', 'avg'];

  protected traceChartOptions = signal<ApexOptions>({});
  /** Jaeger-style duration-vs-time latency chart, binned into count/error-sized bubbles. */
  protected latencyBubbleOptions = signal<ApexOptions>({});

  protected readonly displayedColumns = ['service', 'operation', 'spans', 'status', 'duration', 'time'];
  protected readonly formatDuration = formatDuration;
  protected readonly parseDuration = parseDotnetTimespan;

  private edgeColor(errorRate: number): string {
    if (errorRate >= 0.2) return '#f44336';
    if (errorRate >= 0.05) return '#ff9800';
    return 'var(--mat-sys-outline)';
  }

  private nodeColor(errorRate: number): string {
    if (errorRate >= 0.2) return '#f44336';
    if (errorRate >= 0.05) return '#ff9800';
    return '#4caf50';
  }

  /** Longest duration among the rows currently shown, for scaling inline duration bars. */
  protected maxRowDurationMs = computed(() =>
    this.displayRows().reduce((max, t) => Math.max(max, parseDotnetTimespan(t.traceDuration)), 1)
  );

  protected durationBarPct(t: TraceInfo): number {
    return Math.min(100, (parseDotnetTimespan(t.traceDuration) / this.maxRowDurationMs()) * 100);
  }

  protected readonly serviceColor = serviceColor;

  /** Service-map node click → filter the trace table to that service and switch to it. */
  protected onNodeClick(node: GraphNode): void {
    this.selectedService.set(node.id);
    this.selectedOperation.set('');
    this.pageIndex.set(0);
    this.selectedTab.set(0);
  }

  constructor() {
    // Slide relative preset windows to "now" on (re)entry so navigating back refreshes.
    this.timeRange.refreshRelativeWindow();

    // Tenant-wide, signal-agnostic — fetched once, not on every time-range change.
    this.resourcesApi.getServices().subscribe({
      next: (services) => this.services.set(services),
    });

    // Reload summary + page (in parallel) whenever the time range or any server-side filter
    // changes. asOf resets so a fresh one is captured for the new query (decision 3's handshake).
    effect(() => {
      this.timeRange.range();
      this.filterMode();
      this.selectedService();
      this.selectedOperation();
      this.minDurationMs();
      this.maxDurationMs();
      this.serverQuery();
      untracked(() => {
        this.pageIndex.set(0);
        this.reloadAll();
      });
    });

    // Dependencies (service map): only the Service Map tab needs this.
    effect(() => {
      const tab = this.selectedTab();
      this.timeRange.range();
      untracked(() => {
        if (tab === 1) this.loadMeta();
      });
    });

    // Operation dropdown options: reload when the selected service (or time range) changes.
    effect(() => {
      this.selectedService();
      this.timeRange.range();
      untracked(() => this.loadOperations());
    });

    // Mirror filter/paging/view state into the URL (shareable/deep-linkable). No sort/dir
    // (decision 4) and no `page` (decision 1: keyset paging has no page number); `cursor` isn't
    // persisted across navigation either.
    effect(() => {
      this.urlState.patch({
        mode: this.filterMode() !== 'all' ? this.filterMode() : null,
        service: this.selectedService() || null,
        op: this.selectedOperation() || null,
        q: this.searchText() || null,
        minDur: this.filterMode() === 'slow' ? this.minDurationMs() : null,
        maxDur: this.filterMode() === 'slow' && this.maxDurationMs() > 0 ? this.maxDurationMs() : null,
        chart: this.chartView() !== 'volume' ? this.chartView() : null,
        size: this.pageSize() !== 100 ? this.pageSize() : null,
      });
    });

    // Adopt filter/paging params on back/forward navigation.
    this.urlState.changes().subscribe(() => this.readStateFromUrl());

    effect(() => {
      savePageState(STATE_KEY, {
        filterMode: this.filterMode(),
        selectedService: this.selectedService(),
        selectedOperation: this.selectedOperation(),
        searchText: this.searchText(),
        minDurationMs: this.minDurationMs(),
        maxDurationMs: this.maxDurationMs(),
        pageSize: this.pageSize(),
        chartView: this.chartView(),
      });
    });

    // Chart follows the summary's buckets/latency heatmap.
    effect(() => {
      const s = this.summary();
      const { start, end } = this.timeRange.range();
      untracked(() => {
        this.buildChart(start, end, s?.buckets ?? []);
        this.buildLatencyBubbles(s?.latencyBuckets ?? []);
      });
    });

    // "New since" banner: re-poll the summary every 30s while the tab is visible.
    this.newSincePollHandle = setInterval(() => {
      if (document.visibilityState === 'visible') this.pollSummary();
    }, NEW_SINCE_POLL_MS);
  }

  ngOnDestroy(): void {
    if (this.newSincePollHandle) clearInterval(this.newSincePollHandle);
    this.pageSub?.unsubscribe();
    this.summarySub?.unsubscribe();
  }

  private currentFilter() {
    const { start, end } = this.timeRange.range();
    const slow = this.filterMode() === 'slow';
    return {
      start, end,
      mode: this.filterMode(),
      service: this.selectedService() || undefined,
      operation: this.selectedOperation() || undefined,
      minDurationMs: slow ? this.minDurationMs() : undefined,
      maxDurationMs: slow && this.maxDurationMs() > 0 ? this.maxDurationMs() : undefined,
      q: this.serverQuery() || undefined,
    };
  }

  /** Fires `summary` and `page` in parallel for the first page of a (new) query. */
  private reloadAll(): void {
    this.summaryLoading.set(true);
    this.pageLoading.set(true);
    this.page.set(null);

    this.summarySub?.unsubscribe();
    this.summarySub = this.api.getTraceSummary({ ...this.currentFilter(), bucketCount: BUCKET_COUNT }).subscribe({
      next: (result) => { this.summary.set(result); this.summaryLoading.set(false); },
      error: () => this.summaryLoading.set(false),
    });

    this.pageSub?.unsubscribe();
    this.pageSub = this.api.getTracePage({ ...this.currentFilter(), size: this.pageSize(), nav: 'first' }).subscribe({
      next: (result) => { this.page.set(result); this.pageLoading.set(false); },
      error: () => this.pageLoading.set(false),
    });
  }

  /** Re-polls only the summary, pinned on the query's existing `asOf`, for the "new since" banner. */
  private pollSummary(): void {
    const asOf = this.summary()?.asOf ?? this.page()?.asOf;
    this.api.getTraceSummary({ ...this.currentFilter(), asOf, bucketCount: BUCKET_COUNT }).subscribe({
      next: (result) => this.summary.set(result),
    });
  }

  /** Load the operation list for the operation dropdown (only meaningful with a service selected). */
  private loadOperations(): void {
    const svc = this.selectedService();
    if (!svc) {
      this.operations.set([]);
      if (this.selectedOperation()) this.selectedOperation.set('');
      return;
    }
    const { start, end } = this.timeRange.range();
    this.api.getOperationCounts(svc, start, end).subscribe({
      next: (counts) => this.operations.set(Object.keys(counts).sort()),
    });
  }

  private loadMeta(): void {
    const { start, end } = this.timeRange.range();
    this.api.getDependencies(start, end).subscribe((dependencies) => {
      this.dependencies.set(dependencies);
    });
  }

  private buildChart(start: Date, end: Date, buckets: TraceSummaryResult['buckets']): void {
    const isDark = this.theme.isDark();
    const timestamps = buckets.map((b) => b.timestamp.getTime());

    this.traceChartOptions.set({
      chart: {
        type: 'area', height: 150, background: 'transparent',
        toolbar: { show: false },
        ...timeRangeZoom((start2, end2) => this.timeRange.setCustom(start2, end2)),
      },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: [
        { name: 'Total', data: buckets.map((b, i) => [timestamps[i], b.count]) },
        { name: 'Errors', data: buckets.map((b, i) => [timestamps[i], b.errorCount]) },
      ],
      xaxis: { type: 'datetime', labels: { datetimeUTC: false } },
      colors: ['#2196f3', '#f44336'],
      stroke: { curve: 'smooth', width: 2 },
      fill: { opacity: 0.2 },
      legend: { position: 'top' },
      dataLabels: { enabled: false },
    });
  }

  /**
   * Jaeger-style latency chart: server-computed buckets on a time x log-duration grid, rendered
   * as bubbles sized by trace count and colored by error ratio (see ERROR_TIERS). Clicking a
   * single-trace bubble opens it; clicking a multi-trace bubble zooms into its time span.
   */
  private buildLatencyBubbles(buckets: TraceLatencyBucket[]): void {
    const isDark = this.theme.isDark();
    const { start, end } = this.timeRange.range();

    const tierSeries: { x: number; y: number; z: number; count: number; errorCount: number;
      errorRatio: number; xStart: number; xEnd: number; yStart: number; yEnd: number; sampleTraceIdHex?: string }[][] =
      ERROR_TIERS.map(() => []);
    for (const b of buckets) {
      const errorRatio = b.errorCount / b.count;
      const tierIndex = ERROR_TIERS.findIndex((t) => errorRatio <= t.max);
      const xStart = b.xStart.getTime();
      const xEnd = b.xEnd.getTime();
      tierSeries[tierIndex < 0 ? ERROR_TIERS.length - 1 : tierIndex].push({
        x: (xStart + xEnd) / 2, y: (b.yStartMs + b.yEndMs) / 2, z: Math.sqrt(b.count),
        count: b.count, errorCount: b.errorCount, errorRatio,
        xStart, xEnd, yStart: b.yStartMs, yEnd: b.yEndMs, sampleTraceIdHex: b.sampleTraceIdHex,
      });
    }

    const zoom = timeRangeZoom((s, e) => this.timeRange.setCustom(s, e));
    this.latencyBubbleOptions.set({
      chart: {
        type: 'bubble', height: 200, background: 'transparent', toolbar: { show: false },
        zoom: { ...zoom.zoom, type: 'x' },
        events: {
          ...zoom.events,
          markerClick: (_e, _ctx, cfg: { seriesIndex: number; dataPointIndex: number; w: unknown }) =>
            this.onBubbleClick(cfg),
          dataPointSelection: (_e, _ctx, cfg: { seriesIndex: number; dataPointIndex: number; w: unknown }) =>
            this.onBubbleClick(cfg),
        },
      },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: ERROR_TIERS.map((tier, i) => ({ name: tier.label, data: tierSeries[i] })),
      colors: ERROR_TIERS.map((t) => t.color),
      plotOptions: { bubble: { minBubbleRadius: 4, maxBubbleRadius: 26, zScaling: false } },
      xaxis: { type: 'datetime', min: start.getTime(), max: end.getTime(), labels: { datetimeUTC: false } },
      yaxis: { title: { text: 'Duration' }, labels: { formatter: (v: number) => formatDuration(v) } },
      markers: { strokeWidth: 0, fillOpacity: 0.7 },
      tooltip: {
        custom: ({ seriesIndex, dataPointIndex, w }) => {
          const p = w.config.series[seriesIndex].data[dataPointIndex];
          const pct = (p.errorRatio * 100).toFixed(0);
          return `<div style="padding:6px 8px">
            <b>${p.count}</b> trace${p.count === 1 ? '' : 's'}
            ${p.errorCount ? ` · ${p.errorCount} error${p.errorCount === 1 ? '' : 's'} (${pct}%)` : ''}<br/>
            ${formatDuration(p.yStart)}–${formatDuration(p.yEnd)}<br/>
            ${new Date(p.xStart).toLocaleTimeString()} – ${new Date(p.xEnd).toLocaleTimeString()}
          </div>`;
        },
      },
      grid: { show: true },
      legend: { position: 'top' },
      dataLabels: { enabled: false },
    });
  }

  /** Bubble-click → open a single trace, or zoom into a multi-trace bucket's time span. */
  private onBubbleClick(cfg: { seriesIndex: number; dataPointIndex: number; w: unknown }): void {
    const w = cfg.w as { config: { series: { data: { count: number; sampleTraceIdHex?: string; xStart: number; xEnd: number }[] }[] } };
    const p = w?.config?.series?.[cfg.seriesIndex]?.data?.[cfg.dataPointIndex];
    if (!p) return;
    this.zone.run(() => {
      if (p.count === 1 && p.sampleTraceIdHex) this.navigate(p.sampleTraceIdHex);
      else this.timeRange.setCustom(new Date(p.xStart), new Date(p.xEnd));
    });
  }

  private readNum(key: string): number | null {
    const raw = this.urlState.get(key);
    if (raw == null) return null;
    const n = Number(raw);
    return Number.isFinite(n) ? n : null;
  }

  /** Pull filter state from the URL (back/forward). Idempotent: only differing values are set. */
  private readStateFromUrl(): void {
    const mode = (this.urlState.get('mode') as FilterMode) ?? 'all';
    const service = this.urlState.get('service') ?? '';
    const operation = this.urlState.get('op') ?? '';
    const q = this.urlState.get('q') ?? '';
    const minDur = this.readNum('minDur') ?? this.saved.minDurationMs;
    const maxDur = this.readNum('maxDur') ?? this.saved.maxDurationMs;
    const chart = (this.urlState.get('chart') as ChartView) ?? 'volume';
    const size = this.readNum('size') ?? this.saved.pageSize;
    if (this.filterMode() !== mode) this.filterMode.set(mode);
    if (this.selectedService() !== service) this.selectedService.set(service);
    if (this.selectedOperation() !== operation) this.selectedOperation.set(operation);
    if (this.searchText() !== q) { this.searchText.set(q); this.searchInput.set(q); }
    if (this.minDurationMs() !== minDur) this.minDurationMs.set(minDur);
    if (this.maxDurationMs() !== maxDur) this.maxDurationMs.set(maxDur);
    if (this.chartView() !== chart) this.chartView.set(chart);
    if (this.pageSize() !== size) this.pageSize.set(size);
  }

  // Filter edits reset to the first page.
  protected onModeChange(mode: FilterMode): void { this.filterMode.set(mode); this.pageIndex.set(0); }
  protected onServiceChange(value: string): void {
    this.selectedService.set(value);
    this.selectedOperation.set('');
    this.pageIndex.set(0);
  }
  protected onOperationChange(value: string): void { this.selectedOperation.set(value); this.pageIndex.set(0); }
  protected onSearchChange(value: string): void {
    this.searchInput.set(value);
    this.searchText.set(value);
    this.pageIndex.set(0);
  }
  protected submitSearch(): void { this.onSearchChange(this.searchInput().trim()); }
  protected onMinDurationChange(value: number): void { this.minDurationMs.set(value); this.pageIndex.set(0); }
  protected onMaxDurationChange(value: number): void { this.maxDurationMs.set(value); this.pageIndex.set(0); }
  protected onAnalyticsSort(s: Sort): void {
    this.analyticsSort.set({ col: s.active, dir: (s.direction || 'desc') as SortDir });
  }

  protected setChartView(view: ChartView): void { this.chartView.set(view); }

  protected openSearchHelp(): void {
    this.dialog.open(TraceSearchHelpDialogComponent, { maxWidth: '720px', width: '90vw' });
  }

  protected loadAnalytics(): void {
    const svc = this.analyticsService();
    if (!svc) { this.operationStats.set([]); return; }
    const { start, end } = this.timeRange.range();
    this.api.getOperationStats(svc, start, end).subscribe({
      next: (stats) => this.operationStats.set(stats),
    });
  }

  protected navigate(traceId: string): void {
    this.router.navigate(['/traces', traceId]);
  }

  /**
   * Maps MatPaginator's page event onto a keyset `nav` (decision 1: no arbitrary page jump).
   * `showFirstLastButtons` only ever moves the index by ±1 or straight to the first/last computed
   * page, so the direction is unambiguous from the index delta.
   */
  protected onPage(e: PageEvent): void {
    if (e.pageSize !== this.pageSize()) {
      this.pageSize.set(e.pageSize);
      this.pageIndex.set(0);
      this.fetchPage('first');
      return;
    }

    const lastIndex = Math.max(0, Math.ceil(this.effectiveTotal() / this.pageSize()) - 1);
    let nav: 'first' | 'next' | 'prev' | 'last';
    if (e.pageIndex === 0) nav = 'first';
    else if (!this.totalIsLowerBound() && e.pageIndex >= lastIndex) nav = 'last';
    else if (e.pageIndex > (e.previousPageIndex ?? 0)) nav = 'next';
    else nav = 'prev';

    this.pageIndex.set(e.pageIndex);
    this.fetchPage(nav);
  }

  private fetchPage(nav: 'first' | 'next' | 'prev' | 'last'): void {
    const current = this.page();
    const cursor = nav === 'next' ? current?.nextCursor ?? undefined
      : nav === 'prev' ? current?.prevCursor ?? undefined
      : undefined;

    this.pageLoading.set(true);
    this.pageSub?.unsubscribe();
    this.pageSub = this.api.getTracePage({
      ...this.currentFilter(),
      // Must be the page's own resolved asOf, not summary's — see LogsComponent.fetchPage's
      // identical note (Target API's asOf opaqueness/parallel-request rule).
      asOf: current?.asOf ?? this.summary()?.asOf,
      size: this.pageSize(),
      cursor,
      nav,
    }).subscribe({
      next: (result) => { this.page.set(result); this.pageLoading.set(false); },
      error: () => this.pageLoading.set(false),
    });
  }

  /** Clicking the "new since" banner resets `asOf` to now and returns to the first page. */
  protected resetAsOf(): void {
    this.summary.set(this.summary() ? { ...this.summary()!, asOf: new Date().toISOString(), newSinceAsOf: 0 } : null);
    this.pageIndex.set(0);
    this.reloadAll();
  }

  // =========================================================================
  // EXPORT / PERMALINK
  // =========================================================================

  /**
   * The current page's rows — a full-result export needs the streamed endpoint Phase 7 adds;
   * until then this exports what's on screen, and the export menu is labelled accordingly.
   */
  private exportRows(): TraceInfo[] {
    return this.displayRows();
  }

  /** Download the current page's traces as CSV (one row per trace). */
  protected exportCsv(): void {
    const rows = this.exportRows();
    if (!rows.length) return;
    const headers = ['TraceId', 'Service', 'Operation', 'DurationMs', 'Spans', 'Status', 'StartTime'];
    const data = rows.map((t) => [
      t.traceIdHex,
      t.serviceName ?? '',
      t.rootOperationName ?? '',
      parseDotnetTimespan(t.traceDuration),
      t.spanCount,
      t.hasErrors ? 'ERROR' : 'OK',
      new Date(t.traceStartTime).toISOString(),
    ]);
    downloadCsv(`traces_${fileStamp()}.csv`, headers, data);
  }

  /** Download the current page's traces as raw JSON. */
  protected exportJson(): void {
    const rows = this.exportRows();
    if (!rows.length) return;
    downloadJson(`traces_${fileStamp()}.json`, rows);
  }

  /** Tracks whether a server export is in flight, so the menu can disable itself against a double-click. */
  protected readonly serverExportPending = signal(false);

  /**
   * Server-side streaming export (list-pages-server-side plan, Phase 8): one trace-summary row per
   * trace matching the current filters, not just the current page — replaces the on-screen-only
   * limitation {@link exportCsv}/{@link exportJson}'s doc comments call out. Builds the request from
   * the same {@link currentFilter} every other request on this page uses.
   */
  protected exportServerSide(format: 'ndjson' | 'csv'): void {
    if (this.serverExportPending()) return;
    this.serverExportPending.set(true);
    this.api.getTraceExport(this.currentFilter(), format).subscribe({
      next: (blob) => downloadBlob(`traces-export_${fileStamp()}.${format}`, blob),
      error: () => this.serverExportPending.set(false),
      complete: () => this.serverExportPending.set(false),
    });
  }

  /** Copy a shareable link to the current view (filters + range live in the URL). */
  protected copyLink(): void {
    copyPermalink().then(() => {
      this.linkCopied.set(true);
      setTimeout(() => this.linkCopied.set(false), 1500);
    }).catch(() => {});
  }
}
