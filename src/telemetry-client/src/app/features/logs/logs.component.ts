import { TenantService } from '../../core/services/tenant.service';
import { Component, LOCALE_ID, computed, effect, inject, signal, untracked, OnDestroy } from '@angular/core';
import { DatePipe, DecimalPipe, SlicePipe, PercentPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatPaginatorIntl, MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatChipsModule } from '@angular/material/chips';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { NgApexchartsModule } from 'ng-apexcharts';
import type { ApexOptions } from 'ng-apexcharts';
import { FormsModule } from '@angular/forms';

import { LogsApiService, LogSummaryResult, LogPageResult, LogFacetsResult } from '../../core/services/api/logs-api.service';
import { GroupedPaginatorIntl, lowerBoundTotalLabel } from '../../shared/utils/paginator-intl';
import { SUMMARY_TIMEOUT_TOOLTIP, formatSummaryTotal } from '../../shared/utils/summary-total';
import { ResourcesApiService } from '../../core/services/api/resources-api.service';
import { TimeRangeService } from '../../core/services/time-range.service';
import { ThemeService } from '../../core/services/theme.service';
import { CapabilitiesService } from '../../core/services/capabilities.service';
import { LogRecord, getSeverityLabel, getSeverityColor, getSeverityBg, getServiceName, getTimestamp } from '../../core/models/log.models';
import { StatCardComponent } from '../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { PageHeaderComponent } from '../../shared/components/page-header/page-header.component';
import { buildLogSeriesOptions, timeRangeZoom } from '../../shared/utils/chart.utils';
import { parseSearchQuery, ParsedSearchQuery } from '../../shared/utils/search-query.parser';
import { LogSearchHelpDialogComponent } from './log-search-help-dialog/log-search-help-dialog.component';
import { FacetValueType, Facet } from './facet.models';
import { FacetValuesDialogComponent, FacetValuesDialogData } from './facet-values-dialog/facet-values-dialog.component';
import { loadPageState, savePageState } from '../../shared/utils/page-state';
import { UrlStateService } from '../../shared/utils/url-state';
import { downloadCsv, downloadJson, downloadBlob, copyPermalink, fileStamp } from '../../shared/utils/export.utils';

const BUCKET_COUNT = 60;
const STATE_KEY = 'state.logs';
/** Default number of attribute keys / values per key the faceting sidebar shows (raised via "show more"). */
const FACET_KEY_LIMIT = 15;
const FACET_VALUE_LIMIT = 8;
/** How many fields / values each "show more" click reveals. */
const FACET_KEY_STEP = 15;
const FACET_VALUE_STEP = 10;

function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** Split a search string into its ` AND `-separated terms (empty when blank). */
function splitTerms(query: string): string[] {
  const t = query.trim();
  return t ? t.split(' AND ').map((s) => s.trim()).filter(Boolean) : [];
}

function buildAttributeTerm(key: string, value: string, exclude: boolean): string {
  const needsQuotes = /[\s:="]/.test(value);
  const quoted = needsQuotes ? `"${value.replace(/"/g, '\\"')}"` : value;
  return `${exclude ? '-' : ''}${key}:${quoted}`;
}

/** Heuristic type inference over a server facet's already-stringified values, for the type glyph. */
function inferFacetType(values: { value: string }[]): FacetValueType {
  if (values.length === 0) return 'string';
  const allBool = values.every((v) => v.value === 'true' || v.value === 'false');
  if (allBool) return 'boolean';
  const allNumeric = values.every((v) => v.value !== '' && !isNaN(Number(v.value)));
  return allNumeric ? 'number' : 'string';
}

@Component({
  selector: 'app-logs',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, SlicePipe, PercentPipe, FormsModule, RouterLink,
    MatCardModule, MatTableModule, MatIconModule,
    MatFormFieldModule, MatInputModule, MatSelectModule, MatProgressBarModule,
    MatButtonModule, MatPaginatorModule, MatChipsModule, MatDialogModule,
    MatMenuModule, MatTooltipModule, NgApexchartsModule,
    StatCardComponent, EmptyStateComponent, PageHeaderComponent,
  ],
  // Its own paginator intl, so a lower-bound total can print "of 10,000+" without touching other pages' paginators.
  providers: [{ provide: MatPaginatorIntl, useClass: GroupedPaginatorIntl }],
  templateUrl: './logs.component.html',
  styleUrl: './logs.component.scss',
})
export class LogsComponent implements OnDestroy {
  private readonly api = inject(LogsApiService);
  private readonly resourcesApi = inject(ResourcesApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly theme = inject(ThemeService);
  private readonly dialog = inject(MatDialog);
  private readonly urlState = inject(UrlStateService);
  protected readonly tenant = inject(TenantService);
  private readonly capabilitiesService = inject(CapabilitiesService);

  private readonly saved = loadPageState(STATE_KEY, {
    searchText: '',
    selectedService: '',
    selectedSeverity: -1,
    pageSize: 100,
    facetsCollapsed: true,
  });

  protected summaryLoading = signal(true);
  protected pageLoading = signal(true);
  protected summary = signal<LogSummaryResult | null>(null);
  protected page = signal<LogPageResult | null>(null);
  protected facetsResult = signal<LogFacetsResult | null>(null);
  private pageSub?: Subscription;
  private summarySub?: Subscription;
  private facetsSub?: Subscription;

  /**
   * Trace-id mode — a search-box query that is exactly one trace id, whether typed or arrived via
   * a trace's "View Logs" link — bypasses summary/page/facets entirely and ignores the time range.
   */
  protected traceLogs = signal<LogRecord[]>([]);
  protected traceLogsLoading = signal(false);

  protected capabilities = this.capabilitiesService.capabilities;

  /**
   * Applied query — what parsing, filtering, refetching, the URL and saved state read. Changes
   * only on submit (or a facet toggle), so typing never re-runs anything server-side.
   */
  protected searchText = signal<string>(
    this.urlState.get('traceId') ?? this.urlState.get('q') ?? this.saved.searchText);
  /** Draft text in the search box; applied to `searchText` by `submitSearch()`. */
  protected searchInput = signal<string>(this.searchText());
  protected selectedService = signal<string>(this.urlState.get('service') ?? this.saved.selectedService);
  protected selectedSeverity = signal<number>(this.readNum('severity') ?? this.saved.selectedSeverity);
  protected expandedRow = signal<LogRecord | null>(null);
  /** Transient "Link copied!" affordance for the copy-permalink button. */
  protected linkCopied = signal(false);
  /** Whether the pretty-printed JSON body block in the expanded detail is collapsed. */
  protected bodyCollapsed = signal(false);
  /** Transient "Copied!" affordance for the copy-JSON-body button. */
  protected bodyCopied = signal(false);
  /** Which JSON attribute rows are expanded in the open detail (reset when a new row opens). */
  private readonly expandedAttrKeys = signal<Set<string>>(new Set());
  /** Attribute key whose "Copied!" affordance is showing — only one row shows it at a time. */
  protected copiedAttrKey = signal<string | null>(null);

  // Faceting sidebar: collapse state + which keys are collapsed (all open by default).
  protected facetsCollapsed = signal<boolean>(this.saved.facetsCollapsed);
  private readonly closedFacetKeys = signal<Set<string>>(new Set());
  // Ephemeral faceting UI state (not persisted): field-name filter, how many fields to show,
  // and per-key how many values to show.
  protected fieldFilter = signal('');
  protected facetKeyLimit = signal(FACET_KEY_LIMIT);
  private readonly facetValueLimits = signal<Record<string, number>>({});

  // Surrounding-logs context, keyed by the anchor row currently expanded.
  protected contextRows = signal<LogRecord[]>([]);
  protected contextLoading = signal(false);
  protected contextAnchor = signal<LogRecord | null>(null);

  /** Tracked client-side (decision 1: keyset paging has no server-side page number). Reset on any filter change. */
  protected pageIndex = signal(0);
  protected pageSize = signal(this.readNum('size') ?? this.saved.pageSize);
  protected readonly pageSizeOptions = [100, 250, 500];

  /** Distinct services in range for the dropdown — fetched independently of the paged rows. */
  protected services = signal<string[]>([]);

  protected severityOptions = [
    { num: 9, label: 'Info' },
    { num: 13, label: 'Warn' },
    { num: 17, label: 'Error' },
    { num: 21, label: 'Fatal' },
  ];

  protected parsedQuery = computed<ParsedSearchQuery>(() => parseSearchQuery(this.searchText()));
  protected isTraceIdSearch = computed(() => this.parsedQuery().isTraceIdSearch);

  /** The whole raw search text goes to the server now (decision 10: parsed server-side). */
  private serverQuery = computed(() => this.searchText().trim());

  /** The trace id the search box resolves to, or '' — drives trace-id mode. */
  protected traceIdFilter = computed(() => this.parsedQuery().traceId ?? '');

  /** True while the search box holds a trace id: all of that trace's logs, whatever the time range. */
  protected traceFilterActive = computed(() => this.isTraceIdSearch());

  /** Rows for the current page: the trace-filter's full unbounded set, or the current server page. */
  protected displayRows = computed<LogRecord[]>(() =>
    this.traceFilterActive() ? this.traceLogs() : (this.page()?.items ?? [])
  );

  protected loading = computed(() => this.traceFilterActive() ? this.traceLogsLoading() : this.summaryLoading());
  protected rowsLoading = computed(() => this.traceFilterActive() ? this.traceLogsLoading() : this.pageLoading());

  protected effectiveTotal = computed(() => this.traceFilterActive() ? this.traceLogs().length : (this.summary()?.total ?? 0));
  protected totalIsLowerBound = computed(() => !this.traceFilterActive() && (this.summary()?.totalIsLowerBound ?? false));
  /** The server's histogram ran out of time: the severity cards and the chart have no data (which is not "zero"). */
  protected summaryTimedOut = computed(() => !this.traceFilterActive() && (this.summary()?.timedOut ?? false));
  protected readonly summaryTimeoutTooltip = SUMMARY_TIMEOUT_TOOLTIP;
  private readonly locale = inject(LOCALE_ID);
  /** The paginator's length; a capped total is stretched past the current page while the server offers a next page (as on the trace list). */
  protected paginatorLength = computed(() => {
    const total = this.effectiveTotal();
    if (!this.totalIsLowerBound()) return total;
    const shown = this.pageIndex() * this.pageSize() + (this.page()?.items.length ?? 0);
    return Math.max(total, shown + (this.page()?.nextCursor ? 1 : 0));
  });
  private readonly paginatorIntl = inject(MatPaginatorIntl) as GroupedPaginatorIntl;
  /** The Total Logs card: exact, "10,000+" when capped, "—" when even the capped count timed out. */
  protected totalLabel = computed(() => formatSummaryTotal(this.effectiveTotal(), this.totalIsLowerBound(), this.locale));

  protected errorCount = computed(() => {
    if (this.traceFilterActive()) return this.traceLogs().filter((l) => (l.severityNumber ?? 0) >= 17).length;
    const s = this.summary();
    return s ? s.buckets.reduce((a, b) => a + b.error + b.fatal, 0) : 0;
  });
  protected warnCount = computed(() => {
    if (this.traceFilterActive()) return this.traceLogs().filter((l) => (l.severityNumber ?? 0) >= 13 && (l.severityNumber ?? 0) < 17).length;
    const s = this.summary();
    return s ? s.buckets.reduce((a, b) => a + b.warn, 0) : 0;
  });

  /**
   * Search window limit, explained inline next to the search box (search is unindexed on every
   * provider): shown only when a raw search filter is present and the window exceeds the limit. Fetched via
   * CapabilitiesService, not derived from a failed request, so it shows up front rather than
   * after a 400.
   */
  protected rawSearchWindowMessage = computed<string | null>(() => {
    const caps = this.capabilities();
    if (caps.rawSearchWindowHours == null) return null;
    if (this.parsedQuery().terms.length === 0) return null;
    const { start, end } = this.timeRange.range();
    const hours = (end.getTime() - start.getTime()) / 3_600_000;
    if (hours <= caps.rawSearchWindowHours) return null;
    return `Search is limited to a ${caps.rawSearchWindowHours}-hour window. Narrow the time range or remove the search.`;
  });

  /** Facets adapted from the server's already-counted sample into the sidebar's `Facet` shape. */
  private facetsBase = computed<Facet[]>(() => {
    const result = this.facetsResult();
    if (!result) return [];
    return result.facets.map((f) => {
      const total = f.values.reduce((a, v) => a + v.count, 0);
      const maxCount = Math.max(1, ...f.values.map((v) => v.count));
      return {
        key: f.key,
        type: inferFacetType(f.values),
        distinct: f.values.length,
        total,
        values: f.values.map((v) => ({
          value: v.value,
          count: v.count,
          pct: (v.count / maxCount) * 100,
          share: total > 0 ? v.count / total : 0,
          active: false,
          excluded: false,
        })),
      };
    });
  });

  /** `facetsBase()` decorated with active/excluded per the search box. */
  protected facets = computed<Facet[]>(() => {
    const parts = new Set(splitTerms(this.searchText()));
    return this.facetsBase().map((f) => ({
      ...f,
      values: f.values.map((v) => ({
        ...v,
        active: parts.has(buildAttributeTerm(f.key, v.value, false)),
        excluded: parts.has(buildAttributeTerm(f.key, v.value, true)),
      })),
    }));
  });

  /** Facets after the field-name filter — the full matching set before the display key-limit. */
  protected filteredFacets = computed<Facet[]>(() => {
    const q = this.fieldFilter().trim().toLowerCase();
    return q ? this.facets().filter((f) => f.key.toLowerCase().includes(q)) : this.facets();
  });

  /** Facets actually rendered in the sidebar: filtered, then capped to the current key-limit. */
  protected visibleFacets = computed<Facet[]>(() => this.filteredFacets().slice(0, this.facetKeyLimit()));

  /** "latest N matches" sample-size label for the facets sidebar footer. */
  protected facetSampleSize = computed(() => this.facetsResult()?.sampleSize ?? 0);

  protected chartOptions = signal<ApexOptions>({});

  protected readonly displayedColumns = ['expand', 'time', 'severity', 'service', 'message'];
  protected readonly getSeverityLabel = getSeverityLabel;
  protected readonly getSeverityColor = getSeverityColor;
  protected readonly getSeverityBg = getSeverityBg;
  protected readonly getServiceName = getServiceName;
  protected readonly getTimestamp = getTimestamp;

  constructor() {
    // "of 10,000+" / "of many" while the total is only a lower bound (see paginatorLength).
    effect(() => {
      this.paginatorIntl.totalOverride = this.totalIsLowerBound() ? lowerBoundTotalLabel(this.effectiveTotal(), this.locale) : null;
      this.paginatorIntl.changes.next();
    });

    // Slide relative preset windows to "now" on (re)entry so navigating back refreshes.
    this.timeRange.refreshRelativeWindow();

    // Legacy `?traceId=` links land in the search box as `q` (see the searchText initializer).
    if (this.urlState.get('traceId')) this.urlState.patch({ traceId: null });

    // Tenant-wide, signal-agnostic — fetched once, not on every reload.
    this.resourcesApi.getServices().subscribe({
      next: (services) => this.services.set(services),
    });

    // Reload summary + page (in parallel) whenever the time range or any server-side filter
    // changes, or the trace-id-filter query param toggles. asOf resets so a fresh one is
    // captured for the new query (decision 3's handshake).
    effect(() => {
      const trace = this.traceFilterActive();
      this.selectedService();
      this.selectedSeverity();
      this.serverQuery();
      if (!trace) this.timeRange.range();

      untracked(() => {
        this.pageIndex.set(0);
        if (trace) this.loadByTraceFilter(this.traceIdFilter());
        else this.reloadAll();
      });
    });

    // Facets: fetched only while the sidebar is expanded, re-fetched on filter change.
    effect(() => {
      const collapsed = this.facetsCollapsed();
      const trace = this.traceFilterActive();
      this.selectedService();
      this.selectedSeverity();
      this.serverQuery();
      if (!trace) this.timeRange.range();

      untracked(() => {
        if (!collapsed && !trace) this.loadFacets();
        else this.facetsResult.set(null);
      });
    });

    // Mirror filter state into the URL (shareable/deep-linkable). No `page`/`offset` (decision:
    // keyset paging has no such parameter); the cursor itself isn't persisted across navigation.
    effect(() => {
      this.urlState.patch({
        q: this.searchText() || null,
        service: this.selectedService() || null,
        severity: this.selectedSeverity() >= 0 ? this.selectedSeverity() : null,
        size: this.pageSize() !== 100 ? this.pageSize() : null,
      });
    });

    // Adopt filter params on back/forward navigation.
    this.urlState.changes().subscribe(() => this.readStateFromUrl());

    // Chart follows the summary's buckets.
    effect(() => {
      const s = this.summary();
      untracked(() => this.buildChart(s?.buckets ?? []));
    });

    effect(() => {
      savePageState(STATE_KEY, {
        // A trace-id search is a one-off jump, not a working filter to restore on the next visit.
        searchText: this.isTraceIdSearch() ? '' : this.searchText(),
        selectedService: this.selectedService(),
        selectedSeverity: this.selectedSeverity(),
        pageSize: this.pageSize(),
        facetsCollapsed: this.facetsCollapsed(),
      });
    });
  }

  ngOnDestroy(): void {
    this.pageSub?.unsubscribe();
    this.summarySub?.unsubscribe();
    this.facetsSub?.unsubscribe();
  }

  private currentFilter() {
    const { start, end } = this.timeRange.range();
    return {
      start, end,
      service: this.selectedService() || undefined,
      minSeverity: this.selectedSeverity() >= 0 ? this.selectedSeverity() : undefined,
      q: this.serverQuery() || undefined,
    };
  }

  /** Fires `summary` and `page` in parallel for the first page of a (new) query. */
  private reloadAll(): void {
    this.summaryLoading.set(true);
    this.pageLoading.set(true);
    this.page.set(null);

    this.summarySub?.unsubscribe();
    this.summarySub = this.api.getLogSummary({ ...this.currentFilter(), bucketCount: BUCKET_COUNT }).subscribe({
      next: (result) => { this.summary.set(result); this.summaryLoading.set(false); },
      error: () => this.summaryLoading.set(false),
    });

    this.pageSub?.unsubscribe();
    this.pageSub = this.api.getLogPage({ ...this.currentFilter(), size: this.pageSize(), nav: 'first' }).subscribe({
      next: (result) => { this.page.set(result); this.pageLoading.set(false); },
      error: () => this.pageLoading.set(false),
    });
  }

  private loadFacets(): void {
    this.facetsSub?.unsubscribe();
    this.facetsSub = this.api.getLogFacets(this.currentFilter()).subscribe({
      next: (result) => this.facetsResult.set(result),
    });
  }

  private loadByTraceFilter(traceId: string): void {
    this.traceLogsLoading.set(true);
    this.traceLogs.set([]);
    this.pageSub?.unsubscribe();
    this.pageSub = this.api.getLogsByTrace(traceId).subscribe({
      next: (logs) => {
        this.traceLogs.set(logs);
        this.traceLogsLoading.set(false);
      },
      error: () => this.traceLogsLoading.set(false),
    });
  }

  private buildChart(buckets: LogSummaryResult['buckets']): void {
    const { start, end } = this.timeRange.range();
    const isDark = this.theme.isDark();
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

  /** Pull filter state from the URL (back/forward). Idempotent: only differing values are set. */
  private readStateFromUrl(): void {
    const q = this.urlState.get('q') ?? '';
    const service = this.urlState.get('service') ?? '';
    const severity = this.readNum('severity') ?? -1;
    const size = this.readNum('size') ?? this.saved.pageSize;
    if (this.searchText() !== q) { this.searchText.set(q); this.searchInput.set(q); }
    if (this.selectedService() !== service) this.selectedService.set(service);
    if (this.selectedSeverity() !== severity) this.selectedSeverity.set(severity);
    if (this.pageSize() !== size) this.pageSize.set(size);
  }

  protected toggleRow(row: LogRecord): void {
    this.expandedRow.update((cur) => (cur === row ? null : row));
    this.bodyCollapsed.set(false); // each newly-opened row starts with its JSON body expanded
    this.expandedAttrKeys.set(new Set()); // ...and with every JSON attribute collapsed
    this.copiedAttrKey.set(null);         // don't carry a stale check-mark into the new row
  }

  protected isExpanded(row: LogRecord): boolean {
    return this.expandedRow() === row;
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
    this.pageSub = this.api.getLogPage({
      ...this.currentFilter(),
      // Must be the page's own resolved asOf, not summary's: the cursor being sent was minted
      // against the page response's asOf, and the server rejects a cursor whose filter hash
      // (which covers asOf) doesn't match the request's — summary and page resolve asOf
      // independently when reloadAll() fires them in parallel with neither specifying one, so
      // preferring summary's here would reject every next/prev/last click with a stale-cursor 400.
      asOf: current?.asOf ?? this.summary()?.asOf,
      size: this.pageSize(),
      cursor,
      nav,
    }).subscribe({
      next: (result) => { this.page.set(result); this.pageLoading.set(false); },
      error: () => this.pageLoading.set(false),
    });
  }

  // Filter edits reset to the first page.
  protected onSearchChange(value: string): void {
    this.searchInput.set(value);
    this.searchText.set(value);
  }
  protected submitSearch(): void { this.onSearchChange(this.searchInput().trim()); }
  protected onServiceChange(value: string): void { this.selectedService.set(value); }
  protected onSeverityChange(value: number): void { this.selectedSeverity.set(value); }

  protected openSearchHelp(): void {
    this.dialog.open(LogSearchHelpDialogComponent, { maxWidth: '720px', width: '90vw' });
  }

  /** Drops the trace id from the search box, returning to the time-range-bound list. */
  protected clearTrace(): void {
    this.onSearchChange('');
  }

  // =========================================================================
  // EXPORT / PERMALINK
  // =========================================================================

  /**
   * The current page's rows — a full-result export needs the streamed endpoint Phase 7 adds;
   * until then this exports what's on screen, and the export menu is labelled accordingly.
   */
  private exportRows(): LogRecord[] {
    return this.displayRows();
  }

  /** Download the current page's logs as CSV (one row per log; attributes JSON-encoded). */
  protected exportCsv(): void {
    const rows = this.exportRows();
    if (!rows.length) return;
    const headers = ['Timestamp', 'Severity', 'Service', 'TraceId', 'SpanId', 'Body', 'Attributes'];
    const data = rows.map((l) => [
      getTimestamp(l).toISOString(),
      getSeverityLabel(l.severityNumber),
      getServiceName(l),
      l.traceIdHex ?? '',
      l.spanIdHex ?? '',
      l.bodyValue ?? '',
      JSON.stringify(l.attributes ?? {}),
    ]);
    downloadCsv(`logs_${fileStamp()}.csv`, headers, data);
  }

  /** Download the current page's logs as raw JSON. */
  protected exportJson(): void {
    const rows = this.exportRows();
    if (!rows.length) return;
    downloadJson(`logs_${fileStamp()}.json`, rows);
  }

  /** Tracks whether a server export is in flight, so the menu can disable itself against a double-click. */
  protected readonly serverExportPending = signal(false);

  /**
   * Server-side streaming export (list-pages-server-side plan, Phase 8): every log matching the
   * current filters, not just the current page — replaces the on-screen-only limitation
   * {@link exportCsv}/{@link exportJson}'s doc comments call out. Builds the request from the same
   * {@link currentFilter} every other request on this page uses, so the export always matches what
   * the list/summary are currently showing.
   */
  protected exportServerSide(format: 'ndjson' | 'csv'): void {
    if (this.serverExportPending()) return;
    this.serverExportPending.set(true);
    this.api.getLogExport(this.currentFilter(), format).subscribe({
      next: (blob) => downloadBlob(`logs-export_${fileStamp()}.${format}`, blob),
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

  // =========================================================================
  // FACETING SIDEBAR
  // =========================================================================

  protected toggleFacetsSidebar(): void { this.facetsCollapsed.update((c) => !c); }

  protected isFacetKeyOpen(key: string): boolean { return !this.closedFacetKeys().has(key); }

  protected toggleFacetKey(key: string): void {
    this.closedFacetKeys.update((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      return next;
    });
  }

  /** Toggle a `key:value` (or excluded `-key:value`) facet term in the search box. */
  protected toggleFacetValue(key: string, value: string, exclude: boolean): void {
    const term = buildAttributeTerm(key, value, exclude);
    const opposite = buildAttributeTerm(key, value, !exclude);
    const parts = splitTerms(this.searchText());
    const idx = parts.indexOf(term);
    if (idx >= 0) {
      parts.splice(idx, 1);            // clicking the active term clears it
    } else {
      const oi = parts.indexOf(opposite);
      if (oi >= 0) parts.splice(oi, 1); // flip include <-> exclude rather than stacking both
      parts.push(term);
    }
    this.onSearchChange(parts.join(' AND '));
  }

  /** Step sizes surfaced to the template for the "show more" button labels. */
  protected readonly facetKeyStep = FACET_KEY_STEP;
  protected readonly facetValueStep = FACET_VALUE_STEP;

  /** Material glyph representing an attribute's inferred type. */
  protected facetTypeIcon(t: FacetValueType): string {
    return t === 'number' ? 'tag' : t === 'boolean' ? 'toggle_on' : 'abc';
  }

  /** How many values to render for a key (default FACET_VALUE_LIMIT, raised by "show more"). */
  protected visibleValueCount(key: string): number {
    return this.facetValueLimits()[key] ?? FACET_VALUE_LIMIT;
  }

  /** Reveal FACET_VALUE_STEP more values for one key. */
  protected showMoreValues(key: string): void {
    this.facetValueLimits.update((prev) => ({
      ...prev,
      [key]: this.visibleValueCount(key) + FACET_VALUE_STEP,
    }));
  }

  /** Reveal FACET_KEY_STEP more fields in the sidebar. */
  protected showMoreFields(): void {
    this.facetKeyLimit.update((n) => n + FACET_KEY_STEP);
  }

  /** Open the searchable, virtualized "show all values" dialog for a field. */
  protected openFacetValues(facet: Facet): void {
    this.dialog.open(FacetValuesDialogComponent, {
      data: {
        key: facet.key,
        type: facet.type,
        values: facet.values,
        distinct: facet.distinct,
        total: facet.total,
        searchText: this.searchText,
        toggle: (key: string, value: string, exclude: boolean) => this.toggleFacetValue(key, value, exclude),
      } satisfies FacetValuesDialogData,
      maxWidth: '720px',
      width: '90vw',
    });
  }

  // =========================================================================
  // JSON BODY / ATTRIBUTES (expanded detail)
  // =========================================================================

  /** Parse a log body as a JSON object/array, or null when it isn't structured JSON. */
  private parseJsonBody(body: string | null): unknown | null {
    if (!body) return null;
    const t = body.trim();
    // Only treat objects/arrays as structured — a bare number/string/bool isn't worth a tree.
    if (!(t.startsWith('{') || t.startsWith('['))) return null;
    try {
      const v = JSON.parse(t);
      return v !== null && typeof v === 'object' ? v : null;
    } catch {
      return null;
    }
  }

  /** True when the body is structured JSON worth pretty-printing in the detail. */
  protected isJsonBody(body: string | null): boolean {
    return this.parseJsonBody(body) !== null;
  }

  /** Pretty-printed (2-space) JSON for a structured body; '' when not JSON. */
  protected prettyJsonBody(body: string | null): string {
    const v = this.parseJsonBody(body);
    return v === null ? '' : JSON.stringify(v, null, 2);
  }

  /** Copy the pretty-printed JSON body to the clipboard with a transient affordance. */
  protected copyBody(body: string | null): void {
    const text = this.prettyJsonBody(body);
    if (!text) return;
    navigator.clipboard?.writeText(text).then(() => {
      this.bodyCopied.set(true);
      setTimeout(() => this.bodyCopied.set(false), 1500);
    }).catch(() => {});
  }

  /**
   * Parse an attribute value as structured JSON. Unlike a body, an attribute is typed `unknown`:
   * it may already be an object/array, or a JSON string (which parses on the same rules as a body).
   */
  private parseJsonAttr(value: unknown): unknown | null {
    if (value !== null && typeof value === 'object') return value;
    return typeof value === 'string' ? this.parseJsonBody(value) : null;
  }

  /** True when an attribute value is structured JSON worth its own expander. */
  protected isJsonAttr(value: unknown): boolean {
    return this.parseJsonAttr(value) !== null;
  }

  /** Pretty-printed (2-space) JSON for a structured attribute value; '' when not JSON. */
  protected prettyJsonAttr(value: unknown): string {
    const v = this.parseJsonAttr(value);
    return v === null ? '' : JSON.stringify(v, null, 2);
  }

  /** One-line collapsed preview of a JSON attribute (object values would render `[object Object]`). */
  protected attrPreview(value: unknown): string {
    const v = this.parseJsonAttr(value);
    return v === null ? String(value ?? '') : JSON.stringify(v);
  }

  /** True when any attribute is JSON — drives the caret gutter that keeps all keys aligned. */
  protected hasJsonAttrs(attrs: Record<string, unknown> | null | undefined): boolean {
    return attrs != null && Object.values(attrs).some((v) => this.isJsonAttr(v));
  }

  /**
   * The attribute bag as key-sorted entries. Replaces the `keyvalue` pipe here: the table row is
   * untyped (`any`), so the pipe widened `entry.key` to `unknown` and it couldn't be passed to
   * the per-attribute expand/copy handlers.
   */
  protected attrEntries(attrs: Record<string, unknown> | null | undefined): { key: string; value: unknown }[] {
    if (!attrs) return [];
    return Object.entries(attrs)
      .map(([key, value]) => ({ key, value }))
      .sort((a, b) => a.key.localeCompare(b.key));
  }

  protected isAttrExpanded(key: string): boolean {
    return this.expandedAttrKeys().has(key);
  }

  /** Expand/collapse the pretty-printed JSON under one attribute row. */
  protected toggleAttr(key: string): void {
    this.expandedAttrKeys.update((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      return next;
    });
  }

  /** Copy one attribute's pretty-printed JSON, flagging just that row as copied. */
  protected copyAttr(key: string, value: unknown): void {
    const text = this.prettyJsonAttr(value);
    if (!text) return;
    navigator.clipboard?.writeText(text).then(() => {
      this.copiedAttrKey.set(key);
      // Guard the reset: a later copy of another attribute owns the affordance now.
      setTimeout(() => { if (this.copiedAttrKey() === key) this.copiedAttrKey.set(null); }, 1500);
    }).catch(() => {});
  }

  // =========================================================================
  // SEARCH-MATCH HIGHLIGHTING (message cell)
  // =========================================================================

  /** HTML for a log body with free-text search matches wrapped in <mark>. Escaped, so it is safe. */
  protected highlightBody(body: string | null): string {
    const escaped = escapeHtml(body ?? '');
    const needles = this.parsedQuery().terms
      .filter((t) => !t.isAttributeFilter && !t.negate && t.freeText)
      .map((t) => escapeHtml(t.freeText!))
      .filter((n) => n.length > 0);
    if (needles.length === 0) return escaped;

    const re = new RegExp(needles.map(escapeRegExp).join('|'), 'gi');
    return escaped.replace(re, (m) => `<mark class="search-hit">${m}</mark>`);
  }

  // =========================================================================
  // SURROUNDING-LOGS CONTEXT
  // =========================================================================

  /** Toggle the surrounding-logs context for a row: show it, or hide it if already shown. */
  protected toggleContext(row: LogRecord): void {
    if (this.isContextAnchor(row)) this.hideContext();
    else this.showContext(row);
  }

  protected showContext(anchor: LogRecord): void {
    this.contextAnchor.set(anchor);
    this.contextRows.set([]);
    this.contextLoading.set(true);
    this.api.getLogContext(anchor.timeUnixNano ?? 0, getServiceName(anchor), 10, 10).subscribe({
      next: (rows) => { this.contextRows.set(rows); this.contextLoading.set(false); },
      error: () => this.contextLoading.set(false),
    });
  }

  protected hideContext(): void {
    this.contextAnchor.set(null);
    this.contextRows.set([]);
  }

  /** True for the row that anchored the context request (same timestamp + service). */
  protected isContextAnchor(row: LogRecord): boolean {
    const a = this.contextAnchor();
    return a != null && row.timeUnixNano === a.timeUnixNano && getServiceName(row) === getServiceName(a);
  }
}
