import { Component, NgZone, computed, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe, DecimalPipe, SlicePipe } from '@angular/common';
import { Router } from '@angular/router';
import { forkJoin } from 'rxjs';
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
import { FormsModule } from '@angular/forms';
import { NgxGraphModule } from '@swimlane/ngx-graph';
import { NgApexchartsModule } from 'ng-apexcharts';
import type { ApexOptions } from 'ng-apexcharts';

import { TracesApiService } from '../../../core/services/api/traces-api.service';
import { TimeRangeService } from '../../../core/services/time-range.service';
import { ThemeService } from '../../../core/services/theme.service';
import { TraceInfo, ServiceDependency, OperationStats } from '../../../core/models/trace.models';
import { StatCardComponent } from '../../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { bucketTraces, formatDuration, parseDotnetTimespan, timeRangeZoom } from '../../../shared/utils/chart.utils';
import { parseSearchQuery, ParsedSearchQuery, SearchTerm } from '../../../shared/utils/search-query.parser';
import { TraceSearchHelpDialogComponent } from '../trace-search-help-dialog/trace-search-help-dialog.component';
import { loadPageState, savePageState } from '../../../shared/utils/page-state';
import { UrlStateService } from '../../../shared/utils/url-state';
import { serviceColor } from '../../../shared/utils/service-colors';

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

const STATE_KEY = 'state.traces';
/** Upper bound on traces pulled for the chart/stat overview (and shallow client paging). */
const OVERVIEW_CAP = 1000;

@Component({
  selector: 'app-trace-list',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, SlicePipe, FormsModule,
    MatCardModule, MatPaginatorModule, MatTableModule, MatSortModule, MatTabsModule, MatIconModule,
    MatButtonToggleModule, MatButtonModule, MatSelectModule, MatFormFieldModule,
    MatInputModule, MatProgressBarModule, MatChipsModule, MatTooltipModule,
    MatDialogModule, NgxGraphModule, NgApexchartsModule,
    StatCardComponent, EmptyStateComponent,
  ],
  templateUrl: './trace-list.component.html',
  styleUrl: './trace-list.component.scss',
})
export class TraceListComponent {
  private readonly api = inject(TracesApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly theme = inject(ThemeService);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly urlState = inject(UrlStateService);
  private readonly zone = inject(NgZone);

  private readonly saved = loadPageState(STATE_KEY, {
    filterMode: 'all' as FilterMode,
    selectedService: '',
    selectedOperation: '',
    searchText: '',
    minDurationMs: 500,
    maxDurationMs: 0,
    pageSize: 100,
    sortColumn: '',
    sortDir: '' as SortDir,
    chartView: 'volume' as ChartView,
  });

  protected loading = signal(true);
  /** Bounded overview used for the chart, stat cards, and shallow/client paging. */
  protected overview = signal<TraceInfo[]>([]);
  /** A single server-fetched page, used only when paging beyond the overview window. */
  private serverPage = signal<TraceInfo[]>([]);
  protected total = signal(0);
  protected capped = signal(false);
  protected services = signal<string[]>([]);
  protected dependencies = signal<ServiceDependency[]>([]);
  /** Operations for the selected service, populating the operation dropdown. */
  protected operations = signal<string[]>([]);
  /** Per-operation RED metrics for the Analytics tab. */
  protected operationStats = signal<OperationStats[]>([]);

  protected filterMode = signal<FilterMode>((this.urlState.get('mode') as FilterMode) ?? this.saved.filterMode);
  protected selectedService = signal<string>(this.urlState.get('service') ?? this.saved.selectedService);
  protected selectedOperation = signal<string>(this.urlState.get('op') ?? this.saved.selectedOperation);
  protected searchText = signal<string>(this.urlState.get('q') ?? this.saved.searchText);
  protected minDurationMs = signal<number>(this.readNum('minDur') ?? this.saved.minDurationMs);
  protected maxDurationMs = signal<number>(this.readNum('maxDur') ?? this.saved.maxDurationMs);
  protected analyticsService = signal('');
  protected analyticsSort = signal<{ col: string; dir: SortDir }>({ col: 'count', dir: 'desc' });

  protected sortColumn = signal<string>(this.urlState.get('sort') ?? this.saved.sortColumn);
  protected sortDir = signal<SortDir>((this.urlState.get('dir') as SortDir) ?? this.saved.sortDir);
  protected chartView = signal<ChartView>((this.urlState.get('chart') as ChartView) ?? this.saved.chartView);

  /** Selected tab index (Traces / Service Map / Analytics); driven by service-map node clicks. */
  protected selectedTab = signal(0);

  private firstOverview = true;

  protected parsedQuery = computed<ParsedSearchQuery>(() => parseSearchQuery(this.searchText()));
  protected isTraceIdSearch = computed(() => this.parsedQuery().isTraceIdSearch);

  /** Attribute (`key:value`) terms → server-side all-span tag search; free text stays client-side. */
  private attributeTerms = computed(() => this.parsedQuery().terms.filter((t) => t.isAttributeFilter));
  private freeTextTerms = computed(() => this.parsedQuery().terms.filter((t) => !t.isAttributeFilter));

  /** Tag predicates sent to the server, encoded as `key=value` (exact) / `key:value` (contains). */
  protected serverTags = computed<string[]>(() =>
    this.attributeTerms().map((t) => `${t.key}${t.isExactMatch ? '=' : ':'}${t.value ?? ''}`)
  );
  /** Stable string key so free-text keystrokes don't re-trigger the tag-driven overview reload. */
  private serverTagsKey = computed(() => this.serverTags().join(''));

  /** True when the result must be paged/refined client-side (trace-id or free-text search). */
  protected clientMode = computed(() => this.isTraceIdSearch() || this.freeTextTerms().length > 0);

  /** Client-side refinement — trace-id + free-text only; tag terms are applied server-side. */
  private refined = computed(() => {
    const query = this.parsedQuery();
    const traces = this.overview();
    if (query.isTraceIdSearch) {
      const id = query.traceId!.toLowerCase();
      return traces.filter((t) => t.traceIdHex.toLowerCase() === id);
    }
    const freeText = this.freeTextTerms();
    if (freeText.length > 0) return this.applyParsedSearch(traces, freeText);
    return traces;
  });

  protected pageIndex = signal(this.readNum('page') ?? 0);
  protected pageSize = signal(this.readNum('size') ?? this.saved.pageSize);
  protected readonly pageSizeOptions = [100, 250, 500, 1000];

  /** Real length behind the paginator: server total normally, refined length in client mode. */
  protected effectiveTotal = computed(() => this.clientMode() ? this.refined().length : this.total());

  /** Rows for the current page — a client slice of the overview, or the fetched server page. */
  protected displayTraces = computed(() => {
    const start = this.pageIndex() * this.pageSize();
    if (this.clientMode()) return this.refined().slice(start, start + this.pageSize());
    if (this.needsServerPage()) return this.serverPage();
    return this.overview().slice(start, start + this.pageSize());
  });

  protected onPage(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
  }

  protected totalTraces = computed(() => this.effectiveTotal());
  protected errorCount = computed(() => this.overview().filter((t) => t.hasErrors).length);
  protected errorRate = computed(() =>
    this.overview().length > 0 ? ((this.errorCount() / this.overview().length) * 100).toFixed(1) + '%' : '0%'
  );
  protected avgDuration = computed(() => {
    const ms = this.overview().map((t) => parseDotnetTimespan(t.traceDuration));
    return ms.length > 0 ? formatDuration(ms.reduce((a, b) => a + b, 0) / ms.length) : '—';
  });

  /** Per-service health (error rate + call volume), derived from the dependency edges. */
  private nodeHealth = computed(() => {
    const deps = this.dependencies();
    const map = new Map<string, { errorRate: number; callCount: number }>();
    const services = new Set<string>([
      ...deps.map((d) => d.parentService),
      ...deps.map((d) => d.childService),
    ]);
    for (const svc of services) {
      // Prefer incoming calls (svc as callee) — they reflect errors serving this
      // service; fall back to outgoing calls for source-only services.
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
      // Label encodes avg duration; tooltip-style detail is in the legend.
      label: formatDuration(d.avgDurationMs),
      callCount: d.callCount,
      errorRate: d.errorRate,
      avgDurationMs: d.avgDurationMs,
      color: this.edgeColor(d.errorRate),
      // Thickness encodes call volume (1.5–6px).
      width: 1.5 + (d.callCount / maxCalls) * 4.5,
    }));
  });

  /** RED-metrics rows for the Analytics tab, sorted by the clicked column (default: calls desc). */
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
  /** Jaeger-style duration-vs-time scatter (latency view). */
  protected scatterOptions = signal<ApexOptions>({});
  /** traceId lookup for scatter marker clicks, indexed [seriesIndex][dataPointIndex]. */
  private scatterIds: string[][] = [];

  protected readonly displayedColumns = ['traceId', 'service', 'operation', 'duration', 'spans', 'status', 'time'];
  protected readonly formatDuration = formatDuration;
  protected readonly parseDuration = parseDotnetTimespan;

  private edgeColor(errorRate: number): string {
    if (errorRate >= 0.2) return '#f44336';   // high errors
    if (errorRate >= 0.05) return '#ff9800';  // some errors
    return 'var(--mat-sys-outline)';          // healthy
  }

  /** Solid health color for a service node border (green/orange/red by error rate). */
  private nodeColor(errorRate: number): string {
    if (errorRate >= 0.2) return '#f44336';
    if (errorRate >= 0.05) return '#ff9800';
    return '#4caf50';
  }

  /** Longest duration among the rows currently shown, for scaling inline duration bars. */
  protected maxRowDurationMs = computed(() =>
    Math.max(1, ...this.displayTraces().map((t) => parseDotnetTimespan(t.traceDuration)))
  );

  /** This trace's duration as a percentage of the slowest visible row (0–100). */
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

    // Overview + total: reload when the time range or any server-side filter/sort changes.
    effect(() => {
      this.timeRange.range();
      this.filterMode();
      this.selectedService();
      this.selectedOperation();
      this.minDurationMs();
      this.maxDurationMs();
      this.serverTagsKey();
      this.sortColumn();
      this.sortDir();
      untracked(() => {
        if (!this.firstOverview) this.pageIndex.set(0);
        this.firstOverview = false;
        this.loadOverview();
      });
    });

    // Services + dependencies (service map): reload on time-range change only.
    effect(() => {
      this.timeRange.range();
      untracked(() => this.loadMeta());
    });

    // Operation dropdown options: reload when the selected service (or time range) changes.
    effect(() => {
      this.selectedService();
      this.timeRange.range();
      untracked(() => this.loadOperations());
    });

    // Deep server page: fetch only when paging past the overview window in pure server mode.
    effect(() => {
      this.pageIndex(); this.pageSize();
      this.timeRange.range(); this.filterMode(); this.selectedService(); this.selectedOperation();
      this.minDurationMs(); this.maxDurationMs(); this.serverTagsKey();
      this.sortColumn(); this.sortDir();
      untracked(() => { if (this.needsServerPage()) this.loadServerPage(); });
    });

    // Mirror filter/paging/sort/view state into the URL (shareable/deep-linkable).
    effect(() => {
      this.urlState.patch({
        mode: this.filterMode() !== 'all' ? this.filterMode() : null,
        service: this.selectedService() || null,
        op: this.selectedOperation() || null,
        q: this.searchText() || null,
        minDur: this.filterMode() === 'slow' ? this.minDurationMs() : null,
        maxDur: this.filterMode() === 'slow' && this.maxDurationMs() > 0 ? this.maxDurationMs() : null,
        sort: this.sortColumn() && this.sortDir() ? this.sortColumn() : null,
        dir: this.sortColumn() && this.sortDir() ? this.sortDir() : null,
        chart: this.chartView() !== 'volume' ? this.chartView() : null,
        page: this.pageIndex() > 0 ? this.pageIndex() : null,
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
        sortColumn: this.sortColumn(),
        sortDir: this.sortDir(),
        chartView: this.chartView(),
      });
    });
  }

  /** Whether the current page falls outside the loaded overview and must be fetched from the server. */
  private needsServerPage(): boolean {
    if (this.clientMode()) return false;
    const start = this.pageIndex() * this.pageSize();
    return this.overview().length < this.total() && start + this.pageSize() > this.overview().length;
  }

  /** Shared server-side filter shape for the overview and deep-page fetches. */
  private serverFilters() {
    const slow = this.filterMode() === 'slow';
    const tags = this.serverTags();
    return {
      mode: this.filterMode(),
      service: this.selectedService() || undefined,
      operation: this.selectedOperation() || undefined,
      minDurationMs: slow ? this.minDurationMs() : undefined,
      maxDurationMs: slow && this.maxDurationMs() > 0 ? this.maxDurationMs() : undefined,
      tags: tags.length ? tags : undefined,
      sort: this.sortColumn() || undefined,
      dir: this.sortDir() || undefined,
    };
  }

  private loadOverview(): void {
    this.loading.set(true);
    const { start, end } = this.timeRange.range();
    this.api.searchTraces({
      start, end, ...this.serverFilters(),
      limit: OVERVIEW_CAP, offset: 0,
    }).subscribe({
      next: (res) => {
        this.overview.set(res.items);
        this.total.set(res.total);
        this.capped.set(res.total > res.items.length);
        this.buildChart(start, end);
        this.buildScatter(start, end);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private loadServerPage(): void {
    const { start, end } = this.timeRange.range();
    this.api.searchTraces({
      start, end, ...this.serverFilters(),
      limit: this.pageSize(), offset: this.pageIndex() * this.pageSize(),
    }).subscribe({ next: (res) => this.serverPage.set(res.items) });
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
    forkJoin({
      services: this.api.getServices(start, end),
      dependencies: this.api.getDependencies(start, end),
    }).subscribe(({ services, dependencies }) => {
      this.services.set(services);
      this.dependencies.set(dependencies);
    });
  }

  private buildChart(start: Date, end: Date): void {
    const isDark = this.theme.isDark();
    const buckets = bucketTraces(this.overview(), start, end);
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
   * Jaeger-style latency scatter: each trace a dot at (start time, duration ms), split into an OK
   * and an Error series for color, with span count driving marker size. Clicking a dot opens it.
   */
  private buildScatter(start: Date, end: Date): void {
    const isDark = this.theme.isDark();
    const ok: { x: number; y: number; z: number }[] = [];
    const err: { x: number; y: number; z: number }[] = [];
    const okIds: string[] = [];
    const errIds: string[] = [];

    for (const t of this.overview()) {
      const point = {
        x: new Date(t.traceStartTime).getTime(),
        y: parseDotnetTimespan(t.traceDuration),
        z: t.spanCount,
      };
      if (t.hasErrors) { err.push(point); errIds.push(t.traceIdHex); }
      else { ok.push(point); okIds.push(t.traceIdHex); }
    }
    // Series order must match the [seriesIndex] used by the marker-click lookup.
    this.scatterIds = [okIds, errIds];

    const zoom = timeRangeZoom((s, e) => this.timeRange.setCustom(s, e));
    this.scatterOptions.set({
      chart: {
        type: 'scatter', height: 200, background: 'transparent', toolbar: { show: false },
        zoom: { ...zoom.zoom, type: 'x' },
        events: {
          ...zoom.events,
          // markerClick is the scatter marker event; dataPointSelection covers ApexCharts
          // versions where only the latter fires for scatter series.
          markerClick: (_e, _ctx, cfg: { seriesIndex: number; dataPointIndex: number }) =>
            this.onScatterClick(cfg.seriesIndex, cfg.dataPointIndex),
          dataPointSelection: (_e, _ctx, cfg: { seriesIndex: number; dataPointIndex: number }) =>
            this.onScatterClick(cfg.seriesIndex, cfg.dataPointIndex),
        },
      },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: [
        { name: 'OK', data: ok },
        { name: 'Error', data: err },
      ],
      colors: ['#2196f3', '#f44336'],
      xaxis: { type: 'datetime', min: start.getTime(), max: end.getTime(), labels: { datetimeUTC: false } },
      yaxis: { title: { text: 'Duration' }, labels: { formatter: (v: number) => formatDuration(v) } },
      markers: { size: [5, 6], strokeWidth: 0, fillOpacity: 0.6 },
      tooltip: {
        custom: ({ seriesIndex, dataPointIndex, w }) => {
          const p = w.config.series[seriesIndex].data[dataPointIndex];
          const when = new Date(p.x).toLocaleTimeString();
          return `<div style="padding:6px 8px"><b>${formatDuration(p.y)}</b> · ${p.z} spans<br/>${when}</div>`;
        },
      },
      grid: { show: true },
      legend: { position: 'top' },
      dataLabels: { enabled: false },
    });
  }

  /** Marker-click → open the clicked trace. ApexCharts fires outside Angular, so re-enter the zone. */
  private onScatterClick(seriesIndex: number, dataPointIndex: number): void {
    const id = this.scatterIds[seriesIndex]?.[dataPointIndex];
    if (id) this.zone.run(() => this.navigate(id));
  }

  private applyParsedSearch(traces: TraceInfo[], terms: SearchTerm[]): TraceInfo[] {
    let result = traces;
    for (const term of terms) {
      const negate = term.negate ?? false;
      if (term.isAttributeFilter) {
        const key = term.key!;
        const value = (term.value ?? '').toLowerCase();
        const exact = term.isExactMatch;
        result = result.filter((t) => {
          const attrs = t.rootSpanAttributes;
          let match = false;
          if (attrs && Object.prototype.hasOwnProperty.call(attrs, key)) {
            const v = String(attrs[key] ?? '').toLowerCase();
            match = exact ? v === value : v.includes(value);
          }
          return match !== negate;
        });
      } else {
        const text = (term.freeText ?? '').toLowerCase();
        result = result.filter((t) => {
          const match =
            (t.rootOperationName?.toLowerCase().includes(text) ?? false) ||
            (t.serviceName?.toLowerCase().includes(text) ?? false);
          return match !== negate;
        });
      }
    }
    return result;
  }

  private readNum(key: string): number | null {
    const raw = this.urlState.get(key);
    if (raw == null) return null;
    const n = Number(raw);
    return Number.isFinite(n) ? n : null;
  }

  /** Pull filter/paging state from the URL (back/forward). Idempotent: only differing values are set. */
  private readStateFromUrl(): void {
    const mode = (this.urlState.get('mode') as FilterMode) ?? 'all';
    const service = this.urlState.get('service') ?? '';
    const operation = this.urlState.get('op') ?? '';
    const q = this.urlState.get('q') ?? '';
    const minDur = this.readNum('minDur') ?? this.saved.minDurationMs;
    const maxDur = this.readNum('maxDur') ?? this.saved.maxDurationMs;
    const sortCol = this.urlState.get('sort') ?? '';
    const sortDir = (this.urlState.get('dir') as SortDir) ?? '';
    const chart = (this.urlState.get('chart') as ChartView) ?? 'volume';
    const page = this.readNum('page') ?? 0;
    const size = this.readNum('size') ?? this.saved.pageSize;
    if (this.filterMode() !== mode) this.filterMode.set(mode);
    if (this.selectedService() !== service) this.selectedService.set(service);
    if (this.selectedOperation() !== operation) this.selectedOperation.set(operation);
    if (this.searchText() !== q) this.searchText.set(q);
    if (this.minDurationMs() !== minDur) this.minDurationMs.set(minDur);
    if (this.maxDurationMs() !== maxDur) this.maxDurationMs.set(maxDur);
    if (this.sortColumn() !== sortCol) this.sortColumn.set(sortCol);
    if (this.sortDir() !== sortDir) this.sortDir.set(sortDir);
    if (this.chartView() !== chart) this.chartView.set(chart);
    if (this.pageSize() !== size) this.pageSize.set(size);
    if (this.pageIndex() !== page) this.pageIndex.set(page);
  }

  // Filter edits reset to the first page (user changes; URL restores keep their page).
  protected onModeChange(mode: FilterMode): void { this.filterMode.set(mode); this.pageIndex.set(0); }
  // Switching service invalidates the operation choice (operations are per-service).
  protected onServiceChange(value: string): void {
    this.selectedService.set(value);
    this.selectedOperation.set('');
    this.pageIndex.set(0);
  }
  protected onOperationChange(value: string): void { this.selectedOperation.set(value); this.pageIndex.set(0); }
  protected onSearchChange(value: string): void { this.searchText.set(value); this.pageIndex.set(0); }
  protected onMinDurationChange(value: number): void { this.minDurationMs.set(value); this.pageIndex.set(0); }
  protected onMaxDurationChange(value: number): void { this.maxDurationMs.set(value); this.pageIndex.set(0); }
  protected onAnalyticsSort(s: Sort): void {
    this.analyticsSort.set({ col: s.active, dir: (s.direction || 'desc') as SortDir });
  }

  /** MatSort header click → server-side sort. Empty direction reverts to the mode default order. */
  protected onSort(s: Sort): void {
    this.sortColumn.set(s.direction ? s.active : '');
    this.sortDir.set(s.direction as SortDir);
    this.pageIndex.set(0);
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
}
