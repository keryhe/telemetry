import { Component, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { DatePipe, DecimalPipe, KeyValuePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTable, MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatChipsModule } from '@angular/material/chips';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { NgApexchartsModule } from 'ng-apexcharts';
import type { ApexOptions } from 'ng-apexcharts';
import { FormsModule } from '@angular/forms';

import { LogsApiService } from '../../core/services/api/logs-api.service';
import { TimeRangeService } from '../../core/services/time-range.service';
import { ThemeService } from '../../core/services/theme.service';
import { LogRecord, getSeverityLabel, getSeverityColor, getSeverityBg, getServiceName, getTimestamp } from '../../core/models/log.models';
import { StatCardComponent } from '../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { bucketLogs, buildLogSeriesOptions, timeRangeZoom } from '../../shared/utils/chart.utils';
import { parseSearchQuery, ParsedSearchQuery, SearchTerm } from '../../shared/utils/search-query.parser';
import { LogSearchHelpDialogComponent } from './log-search-help-dialog/log-search-help-dialog.component';
import { loadPageState, savePageState } from '../../shared/utils/page-state';
import { UrlStateService } from '../../shared/utils/url-state';

const BUCKET_COUNT = 30;
const STATE_KEY = 'state.logs';
/** Upper bound on rows pulled for the chart/stat overview (and shallow client paging). */
const OVERVIEW_CAP = 1000;

@Component({
  selector: 'app-logs',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, KeyValuePipe, FormsModule, RouterLink,
    MatCardModule, MatTableModule, MatIconModule,
    MatFormFieldModule, MatInputModule, MatSelectModule, MatProgressBarModule,
    MatButtonModule, MatPaginatorModule, MatChipsModule, MatDialogModule,
    MatTooltipModule, NgApexchartsModule,
    StatCardComponent, EmptyStateComponent,
  ],
  templateUrl: './logs.component.html',
  styleUrl: './logs.component.scss',
})
export class LogsComponent {
  private readonly api = inject(LogsApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly theme = inject(ThemeService);
  private readonly route = inject(ActivatedRoute);
  private readonly dialog = inject(MatDialog);
  private readonly urlState = inject(UrlStateService);

  private readonly saved = loadPageState(STATE_KEY, {
    searchText: '',
    selectedService: '',
    selectedSeverity: -1,
    pageSize: 100,
  });

  protected loading = signal(true);
  /** Bounded, most-recent slice used for the chart, stat cards, and shallow/client paging. */
  protected overview = signal<LogRecord[]>([]);
  /** A single server-fetched page, used only when paging beyond the overview window. */
  private serverPage = signal<LogRecord[]>([]);
  protected total = signal(0);
  protected capped = signal(false);

  protected searchText = signal<string>(this.urlState.get('q') ?? this.saved.searchText);
  protected selectedService = signal<string>(this.urlState.get('service') ?? this.saved.selectedService);
  protected selectedSeverity = signal<number>(this.readNum('severity') ?? this.saved.selectedSeverity);
  protected traceIdFilter = signal('');
  protected expandedRow = signal<LogRecord | null>(null);

  // multiTemplateDataRows only re-evaluates its `when` predicate when the table
  // re-renders its rows, so we must call renderRows() after toggling expansion.
  private readonly table = viewChild(MatTable<LogRecord>);

  protected pageIndex = signal(this.readNum('page') ?? 0);
  protected pageSize = signal(this.readNum('size') ?? this.saved.pageSize);
  protected readonly pageSizeOptions = [100, 250, 500, 1000];

  /** Distinct services in range for the dropdown — fetched independently of the paged rows. */
  protected services = signal<string[]>([]);

  /** Suppress the reset-to-page-0 on the very first load so a shared ?page=N is honored. */
  private firstOverview = true;

  protected severityOptions = [
    { num: 9, label: 'Info' },
    { num: 13, label: 'Warn' },
    { num: 17, label: 'Error' },
    { num: 21, label: 'Fatal' },
  ];

  protected parsedQuery = computed<ParsedSearchQuery>(() => parseSearchQuery(this.searchText()));
  protected isTraceIdSearch = computed(() => this.parsedQuery().isTraceIdSearch);

  // The trace id currently driving a server-side fetch: query-param banner takes
  // precedence, otherwise a trace-id typed into the search box.
  protected activeTraceId = computed<string | null>(() => {
    if (this.traceIdFilter()) return this.traceIdFilter();
    const q = this.parsedQuery();
    return q.isTraceIdSearch ? q.traceId! : null;
  });

  /** Free-text portion of the query offloaded to the server (attribute terms stay client-side). */
  private serverQuery = computed(() =>
    this.parsedQuery().terms.filter((t) => !t.isAttributeFilter).map((t) => t.freeText ?? '').join(' ').trim()
  );
  /** Attribute filters (`key:value`) the API can't express yet — refined client-side over the overview. */
  private attributeTerms = computed(() => this.parsedQuery().terms.filter((t) => t.isAttributeFilter));
  /** True when we must page/filter the overview client-side rather than trusting server offsets. */
  protected clientMode = computed(() => this.activeTraceId() != null || this.attributeTerms().length > 0);

  /** Client-side refinement (trace mode + attribute terms) applied over the overview set. */
  private refined = computed(() => {
    let result = this.overview();
    const terms = this.attributeTerms();
    if (terms.length > 0) result = this.applyParsedSearch(result, terms);
    return result;
  });

  /** Real length behind the paginator: server total normally, refined length in client mode. */
  protected effectiveTotal = computed(() => this.clientMode() ? this.refined().length : this.total());

  /** Rows for the current page — a client slice of the overview, or the fetched server page. */
  protected displayRows = computed(() => {
    const start = this.pageIndex() * this.pageSize();
    if (this.clientMode()) return this.refined().slice(start, start + this.pageSize());
    if (this.needsServerPage()) return this.serverPage();
    return this.overview().slice(start, start + this.pageSize());
  });

  protected errorCount = computed(() => this.overview().filter((l) => (l.severityNumber ?? 0) >= 17).length);
  protected warnCount = computed(() => this.overview().filter((l) => {
    const s = l.severityNumber ?? 0; return s >= 13 && s < 17;
  }).length);

  protected chartOptions = signal<ApexOptions>({});

  protected readonly displayedColumns = ['expand', 'time', 'severity', 'service', 'message'];
  protected readonly getSeverityLabel = getSeverityLabel;
  protected readonly getSeverityColor = getSeverityColor;
  protected readonly getSeverityBg = getSeverityBg;
  protected readonly getServiceName = getServiceName;
  protected readonly getTimestamp = getTimestamp;

  constructor() {
    // Slide relative preset windows to "now" on (re)entry so navigating back refreshes.
    this.timeRange.refreshRelativeWindow();

    const traceId = this.route.snapshot.queryParamMap.get('traceId');
    if (traceId) this.traceIdFilter.set(traceId);

    // Overview + services + total: reload when the time range or any server-side filter changes.
    effect(() => {
      const activeTrace = this.activeTraceId();
      this.selectedService();
      this.selectedSeverity();
      this.serverQuery();
      if (!activeTrace) this.timeRange.range();

      untracked(() => {
        if (!this.firstOverview) this.pageIndex.set(0);
        this.firstOverview = false;
        if (activeTrace) this.loadByTrace(activeTrace);
        else { this.loadOverview(); this.loadServices(); }
      });
    });

    // Deep server page: fetch only when paging past the overview window in pure server mode.
    effect(() => {
      this.pageIndex(); this.pageSize();
      this.selectedService(); this.selectedSeverity(); this.serverQuery();
      if (!this.activeTraceId()) this.timeRange.range();
      untracked(() => { if (this.needsServerPage()) this.loadServerPage(); });
    });

    // Mirror filter/paging state into the URL (shareable/deep-linkable).
    effect(() => {
      this.urlState.patch({
        q: this.searchText() || null,
        service: this.selectedService() || null,
        severity: this.selectedSeverity() >= 0 ? this.selectedSeverity() : null,
        page: this.pageIndex() > 0 ? this.pageIndex() : null,
        size: this.pageSize() !== 100 ? this.pageSize() : null,
      });
    });

    // Adopt filter/paging params on back/forward navigation.
    this.urlState.changes().subscribe(() => this.readStateFromUrl());

    effect(() => {
      savePageState(STATE_KEY, {
        searchText: this.searchText(),
        selectedService: this.selectedService(),
        selectedSeverity: this.selectedSeverity(),
        pageSize: this.pageSize(),
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
    this.api.searchLogs({
      start, end,
      service: this.selectedService() || undefined,
      minSeverity: this.selectedSeverity() >= 0 ? this.selectedSeverity() : undefined,
      q: this.serverQuery() || undefined,
      limit: OVERVIEW_CAP, offset: 0,
    }).subscribe({
      next: (res) => {
        this.overview.set(res.items);
        this.total.set(res.total);
        this.capped.set(res.total > res.items.length);
        this.buildChart();
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private loadServerPage(): void {
    const { start, end } = this.timeRange.range();
    this.api.searchLogs({
      start, end,
      service: this.selectedService() || undefined,
      minSeverity: this.selectedSeverity() >= 0 ? this.selectedSeverity() : undefined,
      q: this.serverQuery() || undefined,
      limit: this.pageSize(), offset: this.pageIndex() * this.pageSize(),
    }).subscribe({ next: (res) => this.serverPage.set(res.items) });
  }

  private loadServices(): void {
    const { start, end } = this.timeRange.range();
    this.api.getServices(start, end).subscribe({ next: (s) => this.services.set(s) });
  }

  private loadByTrace(traceId: string): void {
    this.loading.set(true);
    this.api.getLogsByTrace(traceId).subscribe({
      next: (logs) => {
        this.overview.set(logs);
        this.total.set(logs.length);
        this.capped.set(false);
        this.buildChart();
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private applyParsedSearch(logs: LogRecord[], terms: SearchTerm[]): LogRecord[] {
    let result = logs;
    for (const term of terms) {
      if (term.isAttributeFilter) {
        const key = term.key!;
        const value = (term.value ?? '').toLowerCase();
        const exact = term.isExactMatch;
        result = result.filter((l) => this.matchesAttribute(l, key, value, exact));
      } else {
        const text = (term.freeText ?? '').toLowerCase();
        result = result.filter((l) => (l.bodyValue?.toLowerCase().includes(text)) ?? false);
      }
    }
    return result;
  }

  private matchesAttribute(log: LogRecord, key: string, value: string, exact: boolean): boolean {
    const scopeAttrs = (log.instrumentationScope as { attributes?: Record<string, unknown> } | null)?.attributes;
    for (const bag of [log.attributes, log.resource?.attributes, scopeAttrs]) {
      if (bag && Object.prototype.hasOwnProperty.call(bag, key)) {
        const v = String(bag[key] ?? '').toLowerCase();
        if (exact ? v === value : v.includes(value)) return true;
      }
    }
    return false;
  }

  private buildChart(): void {
    const { start, end } = this.timeRange.range();
    const isDark = this.theme.isDark();
    const buckets = bucketLogs(this.overview(), start, end, BUCKET_COUNT);
    // Use the shared base as-is (datetime axis, legend, visible grid) so this
    // matches the Dashboard log chart, then layer on drag-to-select zoom.
    const base = buildLogSeriesOptions(buckets, isDark, 180);

    this.chartOptions.set({
      ...base,
      chart: {
        ...base.chart!,
        ...timeRangeZoom((start2, end2) => this.timeRange.setCustom(start2, end2)),
      },
      legend: { show: false },
      grid: { show: true },
    });
  }

  private readNum(key: string): number | null {
    const raw = this.urlState.get(key);
    if (raw == null) return null;
    const n = Number(raw);
    return Number.isFinite(n) ? n : null;
  }

  /** Pull filter/paging state from the URL (back/forward). Idempotent: only differing values are set. */
  private readStateFromUrl(): void {
    const q = this.urlState.get('q') ?? '';
    const service = this.urlState.get('service') ?? '';
    const severity = this.readNum('severity') ?? -1;
    const page = this.readNum('page') ?? 0;
    const size = this.readNum('size') ?? this.saved.pageSize;
    if (this.searchText() !== q) this.searchText.set(q);
    if (this.selectedService() !== service) this.selectedService.set(service);
    if (this.selectedSeverity() !== severity) this.selectedSeverity.set(severity);
    if (this.pageSize() !== size) this.pageSize.set(size);
    if (this.pageIndex() !== page) this.pageIndex.set(page);
  }

  protected toggleRow(row: LogRecord): void {
    this.expandedRow.update((cur) => (cur === row ? null : row));
    // Force the table to re-evaluate the detail row's `when` predicate.
    this.table()?.renderRows();
  }

  protected isExpanded(row: LogRecord): boolean {
    return this.expandedRow() === row;
  }

  // Predicate for the multi-template detail row: render it only below the expanded row.
  protected readonly isExpandedRow = (_: number, row: LogRecord): boolean => this.isExpanded(row);

  protected onPage(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
  }

  // Filter edits reset to the first page (user changes; URL restores keep their page).
  protected onSearchChange(value: string): void { this.searchText.set(value); this.pageIndex.set(0); }
  protected onServiceChange(value: string): void { this.selectedService.set(value); this.pageIndex.set(0); }
  protected onSeverityChange(value: number): void { this.selectedSeverity.set(value); this.pageIndex.set(0); }

  protected openSearchHelp(): void {
    this.dialog.open(LogSearchHelpDialogComponent, { maxWidth: '720px', width: '90vw' });
  }

  protected clearTrace(): void {
    this.traceIdFilter.set('');
  }
}
