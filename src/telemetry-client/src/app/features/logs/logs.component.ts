import { Component, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { DatePipe, KeyValuePipe } from '@angular/common';
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
import { bucketLogs, buildLogSeriesOptions } from '../../shared/utils/chart.utils';
import { parseSearchQuery, ParsedSearchQuery, SearchTerm } from '../../shared/utils/search-query.parser';
import { LogSearchHelpDialogComponent } from './log-search-help-dialog/log-search-help-dialog.component';
import { loadPageState, savePageState } from '../../shared/utils/page-state';

const BUCKET_COUNT = 30;

@Component({
  selector: 'app-logs',
  standalone: true,
  imports: [
    DatePipe, KeyValuePipe, FormsModule, RouterLink,
    MatCardModule, MatTableModule, MatIconModule,
    MatFormFieldModule, MatInputModule, MatSelectModule, MatProgressBarModule,
    MatButtonModule, MatPaginatorModule, MatChipsModule, MatDialogModule,
    MatTooltipModule, NgApexchartsModule,
    StatCardComponent, EmptyStateComponent,
  ],
  templateUrl: './logs.component.html',
  styleUrl: './logs.component.scss',
})
const STATE_KEY = 'state.logs';

export class LogsComponent {
  private readonly api = inject(LogsApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly theme = inject(ThemeService);
  private readonly route = inject(ActivatedRoute);
  private readonly dialog = inject(MatDialog);

  private readonly saved = loadPageState(STATE_KEY, {
    searchText: '',
    selectedService: '',
    selectedSeverity: -1,
    pageSize: 100,
  });

  protected loading = signal(true);
  protected logs = signal<LogRecord[]>([]);
  protected searchText = signal(this.saved.searchText);
  protected selectedService = signal(this.saved.selectedService);
  protected selectedSeverity = signal(this.saved.selectedSeverity);
  protected traceIdFilter = signal('');
  protected expandedRow = signal<LogRecord | null>(null);

  // multiTemplateDataRows only re-evaluates its `when` predicate when the table
  // re-renders its rows, so we must call renderRows() after toggling expansion.
  private readonly table = viewChild(MatTable<LogRecord>);

  protected pageIndex = signal(0);
  protected pageSize = signal(this.saved.pageSize);
  protected readonly pageSizeOptions = [100, 250, 500, 1000];

  protected services = computed(() =>
    [...new Set(this.logs().map((l) => getServiceName(l)))].sort()
  );

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

  protected filtered = computed(() => {
    let result = this.logs();
    const svc = this.selectedService();
    const sev = this.selectedSeverity();
    const query = this.parsedQuery();

    if (svc) result = result.filter((l) => getServiceName(l) === svc);
    if (sev >= 0) result = result.filter((l) => (l.severityNumber ?? 0) >= sev);

    // Trace-id searches are already scoped server-side; otherwise apply parsed terms.
    if (!query.isTraceIdSearch && query.terms.length > 0) {
      result = this.applyParsedSearch(result, query.terms);
    }

    return result;
  });

  protected paged = computed(() => {
    const start = this.pageIndex() * this.pageSize();
    return this.filtered().slice(start, start + this.pageSize());
  });

  protected totalCount = computed(() => this.logs().length);
  protected errorCount = computed(() => this.logs().filter((l) => (l.severityNumber ?? 0) >= 17).length);
  protected warnCount = computed(() => this.logs().filter((l) => {
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
    const traceId = this.route.snapshot.queryParamMap.get('traceId');
    if (traceId) this.traceIdFilter.set(traceId);

    // Load on time-range change OR when the active trace id changes. In trace mode
    // we don't read the range, so range changes won't trigger a redundant refetch.
    effect(() => {
      const activeTrace = this.activeTraceId();
      if (activeTrace) {
        untracked(() => this.loadByTrace(activeTrace));
        return;
      }
      this.timeRange.range();
      untracked(() => this.load());
    });

    // Reset to the first page whenever the filtered result set changes.
    effect(() => {
      this.filtered();
      untracked(() => this.pageIndex.set(0));
    });

    effect(() => {
      savePageState(STATE_KEY, {
        searchText: this.searchText(),
        selectedService: this.selectedService(),
        selectedSeverity: this.selectedSeverity(),
        pageSize: this.pageSize(),
      });
    });
  }

  private load(): void {
    this.loading.set(true);
    const { start, end } = this.timeRange.range();
    this.api.getLogs(start, end).subscribe({
      next: (logs) => { this.logs.set(logs); this.buildChart(); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  private loadByTrace(traceId: string): void {
    this.loading.set(true);
    this.api.getLogsByTrace(traceId).subscribe({
      next: (logs) => { this.logs.set(logs); this.buildChart(); this.loading.set(false); },
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
    const buckets = bucketLogs(this.logs(), start, end, BUCKET_COUNT);
    // Use the shared base as-is (datetime axis, legend, visible grid) so this
    // matches the Dashboard log chart, then layer on drag-to-select zoom.
    const base = buildLogSeriesOptions(buckets, isDark, 180);

    this.chartOptions.set({
      ...base,
      chart: {
        ...base.chart!,
        zoom: { enabled: true, type: 'x' },
        events: {
          zoomed: (_ctx, opts) => {
            const xaxis = opts?.xaxis;
            if (xaxis?.min != null && xaxis?.max != null) {
              this.timeRange.setCustom(new Date(xaxis.min), new Date(xaxis.max));
            }
          },
        },
      },
      legend: { show: false },
      grid: { show: true },
    });
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

  protected openSearchHelp(): void {
    this.dialog.open(LogSearchHelpDialogComponent, { maxWidth: '720px', width: '90vw' });
  }

  protected clearTrace(): void {
    this.traceIdFilter.set('');
  }
}
