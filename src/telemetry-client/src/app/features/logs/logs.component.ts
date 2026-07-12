import { Component, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { DatePipe, DecimalPipe, KeyValuePipe, SlicePipe, PercentPipe } from '@angular/common';
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
import { MatMenuModule } from '@angular/material/menu';
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
import { parseSearchQuery, ParsedSearchQuery, SearchTerm, buildAttributeTerm } from '../../shared/utils/search-query.parser';
import { LogSearchHelpDialogComponent } from './log-search-help-dialog/log-search-help-dialog.component';
import { FacetValueType, Facet } from './facet.models';
import { FacetValuesDialogComponent, FacetValuesDialogData } from './facet-values-dialog/facet-values-dialog.component';
import { loadPageState, savePageState } from '../../shared/utils/page-state';
import { UrlStateService } from '../../shared/utils/url-state';
import { downloadCsv, downloadJson, copyPermalink, fileStamp } from '../../shared/utils/export.utils';

const BUCKET_COUNT = 30;
const STATE_KEY = 'state.logs';
/** Upper bound on rows pulled for the chart/stat overview (and shallow client paging). */
const OVERVIEW_CAP = 1000;
/** Default number of attribute keys / values per key the faceting sidebar shows (raised via "show more"). */
const FACET_KEY_LIMIT = 15;
const FACET_VALUE_LIMIT = 8;
/** How many fields / values each "show more" click reveals. */
const FACET_KEY_STEP = 15;
const FACET_VALUE_STEP = 10;
/** Upper bound on values retained per key (protects the "show all" dialog against pathological cardinality). */
const FACET_VALUE_HARD_CAP = 500;

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

@Component({
  selector: 'app-logs',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, KeyValuePipe, SlicePipe, PercentPipe, FormsModule, RouterLink,
    MatCardModule, MatTableModule, MatIconModule,
    MatFormFieldModule, MatInputModule, MatSelectModule, MatProgressBarModule,
    MatButtonModule, MatPaginatorModule, MatChipsModule, MatDialogModule,
    MatMenuModule, MatTooltipModule, NgApexchartsModule,
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
    facetsCollapsed: true,
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
  /** Transient "Link copied!" affordance for the copy-permalink button. */
  protected linkCopied = signal(false);

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

  /**
   * Attribute facets over the currently-filtered rows: each key's full value list with counts,
   * inferred type, per-value bar width (pct) and share, marked active/excluded per the search box.
   * Counts reflect all other active filters (computed over the refined set). Purely client-side
   * over the loaded overview — the key/value display limits are applied downstream, not here.
   */
  protected facets = computed<Facet[]>(() => {
    const rows = this.refined();
    const byKey = new Map<string, Map<string, number>>();
    const keyType = new Map<string, FacetValueType>();
    for (const l of rows) {
      const scopeAttrs = (l.instrumentationScope as { attributes?: Record<string, unknown> } | null)?.attributes;
      for (const bag of [l.attributes, l.resource?.attributes, scopeAttrs]) {
        if (!bag) continue;
        for (const [k, raw] of Object.entries(bag)) {
          if (raw == null || raw === '' || k === 'Timestamp') continue;
          // First-seen non-null type wins (mixed-type keys are rare); default to 'string'.
          if (!keyType.has(k)) {
            const t = typeof raw;
            keyType.set(k, t === 'number' || t === 'boolean' ? t : 'string');
          }
          const value = String(raw);
          let values = byKey.get(k);
          if (!values) { values = new Map(); byKey.set(k, values); }
          values.set(value, (values.get(value) ?? 0) + 1);
        }
      }
    }

    // Mark values already pinned in the search box (include vs exclude).
    const parts = new Set(splitTerms(this.searchText()));

    return [...byKey.entries()]
      .map(([key, values]) => {
        const total = [...values.values()].reduce((a, b) => a + b, 0);
        const maxCount = Math.max(...values.values());
        const all = [...values.entries()]
          .map(([value, count]) => ({
            value, count,
            pct: maxCount > 0 ? (count / maxCount) * 100 : 0,
            share: total > 0 ? count / total : 0,
            active: parts.has(buildAttributeTerm(key, value, false)),
            excluded: parts.has(buildAttributeTerm(key, value, true)),
          }))
          .sort((a, b) => b.count - a.count)
          .slice(0, FACET_VALUE_HARD_CAP);
        return { key, type: keyType.get(key) ?? 'string', values: all, distinct: values.size, total };
      })
      // Most-covering keys first; ties broken alphabetically for stable ordering.
      .sort((a, b) => b.total - a.total || a.key.localeCompare(b.key));
  });

  /** Facets after the field-name filter — the full matching set before the display key-limit. */
  protected filteredFacets = computed<Facet[]>(() => {
    const q = this.fieldFilter().trim().toLowerCase();
    return q ? this.facets().filter((f) => f.key.toLowerCase().includes(q)) : this.facets();
  });

  /** Facets actually rendered in the sidebar: filtered, then capped to the current key-limit. */
  protected visibleFacets = computed<Facet[]>(() => this.filteredFacets().slice(0, this.facetKeyLimit()));

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
        facetsCollapsed: this.facetsCollapsed(),
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
      const negate = term.negate ?? false;
      if (term.isAttributeFilter) {
        const key = term.key!;
        const value = (term.value ?? '').toLowerCase();
        const exact = term.isExactMatch;
        result = result.filter((l) => this.matchesAttribute(l, key, value, exact) !== negate);
      } else {
        const text = (term.freeText ?? '').toLowerCase();
        result = result.filter((l) => ((l.bodyValue?.toLowerCase().includes(text)) ?? false) !== negate);
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

  // =========================================================================
  // EXPORT / PERMALINK
  // =========================================================================

  /** The full current filtered result set (all loaded rows matching the active filters). */
  private exportRows(): LogRecord[] {
    return this.refined();
  }

  /** Download the current filtered logs as CSV (one row per log; attributes JSON-encoded). */
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

  /** Download the current filtered logs as raw JSON. */
  protected exportJson(): void {
    const rows = this.exportRows();
    if (!rows.length) return;
    downloadJson(`logs_${fileStamp()}.json`, rows);
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
