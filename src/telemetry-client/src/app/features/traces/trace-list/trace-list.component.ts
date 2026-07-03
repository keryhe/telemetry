import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
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
import { bucketTraces, formatDuration, parseDotnetTimespan } from '../../../shared/utils/chart.utils';
import { parseSearchQuery, ParsedSearchQuery, SearchTerm } from '../../../shared/utils/search-query.parser';
import { TraceSearchHelpDialogComponent } from '../trace-search-help-dialog/trace-search-help-dialog.component';
import { loadPageState, savePageState } from '../../../shared/utils/page-state';

interface GraphNode { id: string; label: string; }
interface GraphLink {
  id: string; source: string; target: string; label: string;
  callCount: number; errorRate: number; avgDurationMs: number;
  color: string; width: number;
}

const STATE_KEY = 'state.traces';

@Component({
  selector: 'app-trace-list',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, SlicePipe, FormsModule,
    MatCardModule, MatPaginatorModule, MatTableModule, MatTabsModule, MatIconModule,
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

  private readonly saved = loadPageState(STATE_KEY, {
    filterMode: 'all' as 'all' | 'errors' | 'slow',
    selectedService: '',
    searchText: '',
    minDurationMs: 500,
    pageSize: 100,
  });

  protected loading = signal(true);
  protected traces = signal<TraceInfo[]>([]);
  protected services = signal<string[]>([]);
  protected dependencies = signal<ServiceDependency[]>([]);
  protected operationCounts = signal<Record<string, number>>({});
  protected latencies = signal<Record<string, number>>({});

  protected filterMode = signal<'all' | 'errors' | 'slow'>(this.saved.filterMode);
  protected selectedService = signal<string>(this.saved.selectedService);
  protected searchText = signal(this.saved.searchText);
  protected minDurationMs = signal(this.saved.minDurationMs);
  protected analyticsService = signal('');

  protected parsedQuery = computed<ParsedSearchQuery>(() => parseSearchQuery(this.searchText()));
  protected isTraceIdSearch = computed(() => this.parsedQuery().isTraceIdSearch);

  protected filteredTraces = computed(() => {
    const query = this.parsedQuery();
    const traces = this.traces();

    if (query.isTraceIdSearch) {
      const id = query.traceId!.toLowerCase();
      return traces.filter((t) => t.traceIdHex.toLowerCase() === id);
    }
    if (query.terms.length > 0) {
      return this.applyParsedSearch(traces, query.terms);
    }
    return traces;
  });

  protected pageIndex = signal(0);
  protected pageSize = signal(this.saved.pageSize);
  protected readonly pageSizeOptions = [100, 250, 500, 1000];

  protected pagedTraces = computed(() => {
    const start = this.pageIndex() * this.pageSize();
    return this.filteredTraces().slice(start, start + this.pageSize());
  });

  protected onPage(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
  }

  protected totalTraces = computed(() => this.traces().length);
  protected errorCount = computed(() => this.traces().filter((t) => t.hasErrors).length);
  protected errorRate = computed(() =>
    this.totalTraces() > 0 ? ((this.errorCount() / this.totalTraces()) * 100).toFixed(1) + '%' : '0%'
  );
  protected avgDuration = computed(() => {
    const ms = this.traces().map((t) => parseDotnetTimespan(t.traceDuration));
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

  protected readonly displayedColumns = ['traceId', 'service', 'operation', 'duration', 'spans', 'status', 'time'];
  protected readonly formatDuration = formatDuration;
  protected readonly parseDuration = parseDotnetTimespan;

  private edgeColor(errorRate: number): string {
    if (errorRate >= 0.2) return '#f44336';   // high errors
    if (errorRate >= 0.05) return '#ff9800';  // some errors
    return 'var(--mat-sys-outline)';          // healthy
  }

  constructor() {
    effect(() => {
      this.timeRange.range();
      untracked(() => this.load());
    });
    effect(() => {
      this.filteredTraces();
      untracked(() => this.pageIndex.set(0));
    });
    effect(() => {
      savePageState(STATE_KEY, {
        filterMode: this.filterMode(),
        selectedService: this.selectedService(),
        searchText: this.searchText(),
        minDurationMs: this.minDurationMs(),
        pageSize: this.pageSize(),
      });
    });
  }

  private load(): void {
    this.loading.set(true);
    const { start, end } = this.timeRange.range();

    forkJoin({
      traces: this.api.getTraces({
        start, end, limit: 200,
        mode: this.filterMode(),
        service: this.selectedService() || undefined,
        minDurationMs: this.filterMode() === 'slow' ? this.minDurationMs() : undefined,
      }),
      services: this.api.getServices(start, end),
      dependencies: this.api.getDependencies(start, end),
    }).subscribe({
      next: ({ traces, services, dependencies }) => {
        this.traces.set(traces);
        this.services.set(services);
        this.dependencies.set(dependencies);
        this.buildChart(start, end);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private buildChart(start: Date, end: Date): void {
    const isDark = this.theme.isDark();
    const buckets = bucketTraces(this.traces(), start, end);
    const timestamps = buckets.map((b) => b.timestamp.getTime());

    this.traceChartOptions.set({
      chart: {
        type: 'area', height: 150, background: 'transparent',
        toolbar: { show: false },
        zoom: { enabled: true, type: 'x', allowMouseWheelZoom: false },
        events: {
          zoomed: (_ctx, opts) => {
            const xaxis = opts?.xaxis;
            if (xaxis?.min != null && xaxis?.max != null) {
              this.timeRange.setCustom(new Date(xaxis.min), new Date(xaxis.max));
            }
          },
        },
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

  protected applyFilter(): void {
    this.load();
  }

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
