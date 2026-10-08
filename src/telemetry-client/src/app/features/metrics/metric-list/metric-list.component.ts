import { TenantService } from '../../../core/services/tenant.service';
import { Component, computed, effect, inject, signal, untracked, OnDestroy } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { FormsModule } from '@angular/forms';

import { MetricsApiService } from '../../../core/services/api/metrics-api.service';
import { ResourcesApiService } from '../../../core/services/api/resources-api.service';
import { TimeRangeService } from '../../../core/services/time-range.service';
import {
  MetricCatalogGroupBy, MetricCatalogPage, MetricType, MetricsSummary,
  TYPE_LABELS, getTypeColor, getTypeLabel,
} from '../../../core/models/metric.models';
import { StatCardComponent } from '../../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { PageHeaderComponent } from '../../../shared/components/page-header/page-header.component';
import { ListCapNoticeComponent } from '../../../shared/components/list-cap-notice/list-cap-notice.component';
import { CapabilitiesService } from '../../../core/services/capabilities.service';
import { formatUnitLabel } from '../../../shared/utils/chart.utils';
import { loadPageState, savePageState } from '../../../shared/utils/page-state';
import { UrlStateService } from '../../../shared/utils/url-state';

const STATE_KEY = 'state.metrics';

@Component({
  selector: 'app-metric-list',
  standalone: true,
  imports: [
    DatePipe, FormsModule,
    MatCardModule, MatTableModule, MatIconModule, MatButtonToggleModule,
    MatSelectModule, MatFormFieldModule, MatInputModule,
    MatProgressBarModule, MatChipsModule, MatButtonModule,
    MatTooltipModule,
    StatCardComponent, EmptyStateComponent, PageHeaderComponent, ListCapNoticeComponent,
  ],
  templateUrl: './metric-list.component.html',
  styleUrl: './metric-list.component.scss',
})
export class MetricListComponent implements OnDestroy {
  private readonly api = inject(MetricsApiService);
  private readonly resourcesApi = inject(ResourcesApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly router = inject(Router);
  protected readonly tenant = inject(TenantService);
  private readonly urlState = inject(UrlStateService);

  private readonly saved = loadPageState(STATE_KEY, {
    searchText: '', selectedService: '', selectedType: -1 as MetricType | -1,
    groupBy: 'name' as MetricCatalogGroupBy,
  });

  protected summaryLoading = signal(true);
  protected listLoading = signal(true);
  /** True (unbounded) distinct-metric-name counts per type — backs the count stat cards. */
  protected summary = signal<MetricsSummary>({ uniqueMetricCount: 0, countsByType: [] });
  protected list = signal<MetricCatalogPage | null>(null);
  private listSub?: Subscription;
  private summarySub?: Subscription;

  /** Applied query — what filtering, refetching and saved state read. Changes only on submit. */
  protected searchText = signal<string>(this.urlState.get('q') ?? this.saved.searchText);
  /** Draft text in the search box; applied to `searchText` by `submitSearch()`. */
  protected searchInput = signal<string>(this.searchText());
  protected selectedService = signal<string>(this.urlState.get('service') ?? this.saved.selectedService);
  protected selectedType = signal<MetricType | -1>(this.readNum('type') ?? this.saved.selectedType);
  protected groupBy = signal<MetricCatalogGroupBy>(
    (this.urlState.get('groupBy') as MetricCatalogGroupBy | null) ?? this.saved.groupBy,
  );
  protected readonly capabilities = inject(CapabilitiesService).capabilities;

  protected readonly typeLabels = TYPE_LABELS;
  protected readonly MetricType = MetricType;
  protected readonly metricTypes = Object.values(MetricType).filter((v) => typeof v === 'number') as MetricType[];

  protected services = signal<string[]>([]);

  /** More rows matched than the catalog holds, so say so above it. */
  protected truncated = computed(() => this.list()?.truncated ?? false);
  protected items = computed(() => this.list()?.items ?? []);
  protected names = computed(() => this.list()?.names ?? []);

  // True distinct metric-name counts per type across the full catalog (not just the rows listed).
  private countFor(...types: MetricType[]): number {
    const counts = this.summary().countsByType;
    return types.reduce((sum, t) => sum + (counts.find((c) => c.type === t)?.count ?? 0), 0);
  }
  protected gaugeCount = computed(() => this.countFor(MetricType.Gauge));
  protected counterCount = computed(() => this.countFor(MetricType.Sum));
  // Both OTLP histogram flavours — explicit-bucket and exponential — count as histograms here.
  protected histogramCount = computed(() =>
    this.countFor(MetricType.Histogram, MetricType.ExponentialHistogram)
  );

  /** Unit text for the Unit column: empty for no unit or a braced annotation like `{requests}`. */
  protected readonly unitLabel = formatUnitLabel;

  protected readonly uniqueCols = ['name', 'type', 'unit', 'instances', 'services', 'lastSeen'];
  protected readonly allCols = ['name', 'type', 'unit', 'service', 'lastSeen'];
  protected readonly typeLabel = getTypeLabel;
  protected readonly typeColor = getTypeColor;

  constructor() {
    // Slide relative preset windows to "now" on (re)entry so navigating back refreshes.
    this.timeRange.refreshRelativeWindow();

    // Tenant-wide, signal-agnostic — fetched once, not derived from the loaded rows.
    this.resourcesApi.getServices().subscribe({
      next: (services) => this.services.set(services),
    });

    // Filter/window edits refetch both summary and catalog.
    effect(() => {
      this.timeRange.range();
      this.searchText();
      this.selectedService();
      this.selectedType();
      this.groupBy();
      untracked(() => this.reloadAll());
    });

    // Mirror filter state into the URL (shareable/deep-linkable).
    effect(() => {
      this.urlState.patch({
        q: this.searchText() || null,
        service: this.selectedService() || null,
        type: this.selectedType() >= 0 ? this.selectedType() : null,
        groupBy: this.groupBy() !== 'instance' ? this.groupBy() : null,
      });
    });

    // Adopt filter params on back/forward navigation.
    this.urlState.changes().subscribe(() => this.readStateFromUrl());

    // Persist filter/view state as the fallback for a plain (no-query-param) revisit.
    effect(() => {
      savePageState(STATE_KEY, {
        searchText: this.searchText(),
        selectedService: this.selectedService(),
        selectedType: this.selectedType(),
        groupBy: this.groupBy(),
      });
    });
  }

  ngOnDestroy(): void {
    this.listSub?.unsubscribe();
    this.summarySub?.unsubscribe();
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
    const type = this.readNum('type') ?? -1;
    const groupBy = (this.urlState.get('groupBy') as MetricCatalogGroupBy | null) ?? this.saved.groupBy;
    if (this.searchText() !== q) { this.searchText.set(q); this.searchInput.set(q); }
    if (this.selectedService() !== service) this.selectedService.set(service);
    if (this.selectedType() !== type) this.selectedType.set(type as MetricType | -1);
    if (this.groupBy() !== groupBy) this.groupBy.set(groupBy);
  }

  private currentFilter() {
    const { start, end } = this.timeRange.range();
    return {
      start, end,
      q: this.searchText().trim() || undefined,
      service: this.selectedService() || undefined,
      type: this.selectedType() >= 0 ? (this.selectedType() as MetricType) : undefined,
      groupBy: this.groupBy(),
    };
  }

  private reloadAll(): void {
    this.summaryLoading.set(true);
    this.listLoading.set(true);
    const { start, end } = this.timeRange.range();

    this.summarySub?.unsubscribe();
    this.summarySub = this.api.getMetricsSummary(start, end).subscribe({
      next: (summary) => { this.summary.set(summary); this.summaryLoading.set(false); },
      error: () => this.summaryLoading.set(false),
    });

    this.listSub?.unsubscribe();
    this.listSub = this.api.getCatalog(this.currentFilter()).subscribe({
      next: (result) => { this.list.set(result); this.listLoading.set(false); },
      error: () => this.listLoading.set(false),
    });
  }

  protected navigate(name: string): void {
    this.router.navigate(this.tenant.link('metrics', encodeURIComponent(name)));
  }

  protected submitSearch(): void { this.searchText.set(this.searchInput().trim()); }

  protected setGroupBy(value: MetricCatalogGroupBy): void { this.groupBy.set(value); }
}
