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
import { TraceInfo, ServiceDependency } from '../../../core/models/trace.models';
import { StatCardComponent } from '../../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { bucketTraces, formatDuration, parseDotnetTimespan, timeRangeZoom } from '../../../shared/utils/chart.utils';
import { parseSearchQuery, ParsedSearchQuery, SearchTerm } from '../../../shared/utils/search-query.parser';
import { TraceSearchHelpDialogComponent } from '../trace-search-help-dialog/trace-search-help-dialog.component';
import { loadPageState, savePageState } from '../../../shared/utils/page-state';
import { UrlStateService } from '../../../shared/utils/url-state';

interface GraphNode { id: string; label: string; }
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
    searchText: '',
    minDurationMs: 500,
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
  protected operationCounts = signal<Record<string, number>>({});
  protected latencies = signal<Record<string, number>>({});

  protected filterMode = signal<FilterMode>((this.urlState.get('mode') as FilterMode) ?? this.saved.filterMode);
  protected selectedService = signal<string>(this.urlState.get('service') ?? this.saved.selectedService);
  protected searchText = signal<string>(this.urlState.get('q') ?? this.saved.searchText);
  protected minDurationMs = signal<number>(this.readNum('minDur') ?? this.saved.minDurationMs);
  protected analyticsService = signal('');

  protected sortColumn = signal<string>(this.urlState.get('sort') ?? this.saved.sortColumn);
  protected sortDir = signal<SortDir>((this.urlState.get('dir') as SortDir) ?? this.saved.sortDir);
  protected chartView = signal<ChartView>((this.urlState.get('chart') as ChartView) ?? this.saved.chartView);

  private firstOverview = true;

  protected parsedQuery = computed<ParsedSearchQuery>(() => parseSearchQuery(this.searchText()));
  protected isTraceIdSearch = computed(() => this.parsedQuery().isTraceIdSearch);

  /** True when a search query is active — traces search is refined client-side over the overview. */
  protected clientMode = computed(() => this.isTraceIdSearch() || this.parsedQuery().terms.length > 0);

  /** Client-side refinement (trace-id + operation/service/attribute search) over the overview set. */
  private refined = computed(() => {
    const query = this.parsedQuery();
    const traces = this.overview();
    if (query.isTraceIdSearch) {
      const id = query.traceId!.toLowerCase();
      return traces.filter((t) => t.traceIdHex.toLowerCase() === id);
    }
    if (query.terms.length > 0) return this.applyParsedSearch(traces, query.terms);
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

  protected graphNodes = computed<GraphNode[]>(() =>
    [...new Set([
      ...this.dependencies().map((d) => d.parentService),
      ...this.dependencies().map((d) => d.childService),
    ])].map((s) => ({ id: s, label: s }))
  );

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

  // Cap analytics to the top 10 operations, matching Blazor.
  protected operationRows = computed(() =>
    Object.entries(this.operationCounts())
      .map(([op, count]) => ({ op, count, avgMs: this.latencies()[op] ?? 0 }))
      .sort((a, b) => b.count - a.count)
      .slice(0, 10)
  );

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

  constructor() {
    // Slide relative preset windows to "now" on (re)entry so navigating back refreshes.
    this.timeRange.refreshRelativeWindow();

    // Overview + total: reload when the time range or any server-side filter/sort changes.
    effect(() => {
      this.timeRange.range();
      this.filterMode();
      this.selectedService();
      this.minDurationMs();
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

    // Deep server page: fetch only when paging past the overview window in pure server mode.
    effect(() => {
      this.pageIndex(); this.pageSize();
      this.timeRange.range(); this.filterMode(); this.selectedService(); this.minDurationMs();
      this.sortColumn(); this.sortDir();
      untracked(() => { if (this.needsServerPage()) this.loadServerPage(); });
    });

    // Mirror filter/paging/sort/view state into the URL (shareable/deep-linkable).
    effect(() => {
      this.urlState.patch({
        mode: this.filterMode() !== 'all' ? this.filterMode() : null,
        service: this.selectedService() || null,
        q: this.searchText() || null,
        minDur: this.filterMode() === 'slow' ? this.minDurationMs() : null,
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
        searchText: this.searchText(),
        minDurationMs: this.minDurationMs(),
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

  private loadOverview(): void {
    this.loading.set(true);
    const { start, end } = this.timeRange.range();
    this.api.searchTraces({
      start, end,
      mode: this.filterMode(),
      service: this.selectedService() || undefined,
      minDurationMs: this.filterMode() === 'slow' ? this.minDurationMs() : undefined,
      sort: this.sortColumn() || undefined,
      dir: this.sortDir() || undefined,
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
      start, end,
      mode: this.filterMode(),
      service: this.selectedService() || undefined,
      minDurationMs: this.filterMode() === 'slow' ? this.minDurationMs() : undefined,
      sort: this.sortColumn() || undefined,
      dir: this.sortDir() || undefined,
      limit: this.pageSize(), offset: this.pageIndex() * this.pageSize(),
    }).subscribe({ next: (res) => this.serverPage.set(res.items) });
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
      if (term.isAttributeFilter) {
        const key = term.key!;
        const value = (term.value ?? '').toLowerCase();
        const exact = term.isExactMatch;
        result = result.filter((t) => {
          const attrs = t.rootSpanAttributes;
          if (attrs && Object.prototype.hasOwnProperty.call(attrs, key)) {
            const v = String(attrs[key] ?? '').toLowerCase();
            return exact ? v === value : v.includes(value);
          }
          return false;
        });
      } else {
        const text = (term.freeText ?? '').toLowerCase();
        result = result.filter((t) =>
          (t.rootOperationName?.toLowerCase().includes(text) ?? false) ||
          (t.serviceName?.toLowerCase().includes(text) ?? false)
        );
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
    const q = this.urlState.get('q') ?? '';
    const minDur = this.readNum('minDur') ?? this.saved.minDurationMs;
    const sortCol = this.urlState.get('sort') ?? '';
    const sortDir = (this.urlState.get('dir') as SortDir) ?? '';
    const chart = (this.urlState.get('chart') as ChartView) ?? 'volume';
    const page = this.readNum('page') ?? 0;
    const size = this.readNum('size') ?? this.saved.pageSize;
    if (this.filterMode() !== mode) this.filterMode.set(mode);
    if (this.selectedService() !== service) this.selectedService.set(service);
    if (this.searchText() !== q) this.searchText.set(q);
    if (this.minDurationMs() !== minDur) this.minDurationMs.set(minDur);
    if (this.sortColumn() !== sortCol) this.sortColumn.set(sortCol);
    if (this.sortDir() !== sortDir) this.sortDir.set(sortDir);
    if (this.chartView() !== chart) this.chartView.set(chart);
    if (this.pageSize() !== size) this.pageSize.set(size);
    if (this.pageIndex() !== page) this.pageIndex.set(page);
  }

  // Filter edits reset to the first page (user changes; URL restores keep their page).
  protected onModeChange(mode: FilterMode): void { this.filterMode.set(mode); this.pageIndex.set(0); }
  protected onServiceChange(value: string): void { this.selectedService.set(value); this.pageIndex.set(0); }
  protected onSearchChange(value: string): void { this.searchText.set(value); this.pageIndex.set(0); }
  protected onMinDurationChange(value: number): void { this.minDurationMs.set(value); this.pageIndex.set(0); }

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
    if (!svc) return;
    const { start, end } = this.timeRange.range();
    forkJoin({
      counts: this.api.getOperationCounts(svc, start, end),
      latencies: this.api.getLatencies(svc, start, end),
    }).subscribe(({ counts, latencies }) => {
      this.operationCounts.set(counts);
      this.latencies.set(latencies);
    });
  }

  protected navigate(traceId: string): void {
    this.router.navigate(['/traces', traceId]);
  }
}
