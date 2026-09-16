import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { DecimalPipe, NgClass, PercentPipe } from '@angular/common';
import { Router } from '@angular/router';
import { Subscription, interval, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';

import { GlobalApiService, GlobalTenantStats } from '../../core/services/api/global-api.service';
import { TenantService } from '../../core/services/tenant.service';
import { TimeRangeService, recommendedRefreshIntervalMs } from '../../core/services/time-range.service';
import { PageHeaderComponent } from '../../shared/components/page-header/page-header.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { SparklineShape, buildSparklineShape, formatDuration } from '../../shared/utils/chart.utils';
import { loadPageState, savePageState } from '../../shared/utils/page-state';
import {
  HEALTH_THRESHOLDS_TOKEN, HealthColor, TENANT_STATUS_ORDER, TenantStatus,
  classifyErrorRate, classifyLogErrorRate, classifyP95, hasEnoughSamples, hasSlowTail,
  tenantStatus,
} from '../../shared/config/health-thresholds';

const STATE_KEY = 'state.globalDashboard';

type SortKey = 'health' | 'name' | 'traffic' | 'errors' | 'latency';

/**
 * A tenant's stats plus everything the card renders that is derived from them. Computed once per
 * tenant per load rather than in the template: the status word, the colours and the sort order
 * all depend on the same classification, and recomputing it per binding would let them disagree.
 */
export interface TenantCard {
  stats: GlobalTenantStats;
  status: TenantStatus;
  errorRate: number;
  errorColor: HealthColor;
  p95Color: HealthColor;
  logErrorColor: HealthColor;
  /** False below the sample floor: the p95 is shown as unavailable rather than as a number. */
  showP95: boolean;
  showTail: boolean;
  tracesPerMinute: number;
  /** Null when there is nothing to plot; the card draws a flat dashed rule instead. */
  sparkline: SparklineShape | null;
  /** Drives the line's colour through a CSS class, so it follows the theme like every other value. */
  sparklineTone: HealthColor;
}

@Component({
  selector: 'app-global-dashboard',
  standalone: true,
  imports: [
    DecimalPipe, NgClass, PercentPipe,
    MatCardModule, MatIconModule, MatButtonModule, MatProgressBarModule,
    MatSlideToggleModule, MatTooltipModule, MatFormFieldModule, MatSelectModule,
    PageHeaderComponent, EmptyStateComponent,
  ],
  templateUrl: './global-dashboard.component.html',
  styleUrl: './global-dashboard.component.scss',
})
export class GlobalDashboardComponent {
  private readonly api = inject(GlobalApiService);
  private readonly tenantService = inject(TenantService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly thresholds = inject(HEALTH_THRESHOLDS_TOKEN);

  private readonly saved = loadPageState(STATE_KEY, {
    sortBy: 'health' as SortKey,
    autoRefresh: false,
  });

  protected loading = signal(true);
  protected autoRefresh = signal(this.saved.autoRefresh);
  protected sortBy = signal<SortKey>(this.saved.sortBy);
  protected readonly preset = computed(() => this.timeRange.range().preset);
  private refreshSub?: Subscription;

  private tenants = signal<GlobalTenantStats[]>([]);

  protected readonly sortOptions: { value: SortKey; label: string }[] = [
    { value: 'health', label: 'Health' },
    { value: 'name', label: 'Name' },
    { value: 'traffic', label: 'Traffic' },
    { value: 'errors', label: 'Error rate' },
    { value: 'latency', label: 'p95 latency' },
  ];

  protected readonly formatDuration = formatDuration;

  /** One card model per tenant, already classified and sorted. */
  protected readonly cards = computed<TenantCard[]>(() => {
    const t = this.thresholds;

    const cards = this.tenants().map((stats) => {
      const errorRate = stats.traceCount > 0 ? stats.errorCount / stats.traceCount : 0;
      const status = tenantStatus(
        {
          traceCount: stats.traceCount,
          errorCount: stats.errorCount,
          p95Ms: stats.p95Ms,
          p99Ms: stats.p99Ms,
          lastSeenUtc: stats.lastSeenUtc,
          failed: stats.failed,
        },
        t,
      );
      const showP95 = hasEnoughSamples(stats.traceCount, t);

      return {
        stats,
        status,
        errorRate,
        errorColor: classifyErrorRate(errorRate, t),
        p95Color: classifyP95(stats.p95Ms, stats.traceCount, t),
        logErrorColor: classifyLogErrorRate(stats.logErrorCount, stats.logCount, t),
        showP95,
        showTail: showP95 && hasSlowTail(stats.p95Ms, stats.p99Ms, t),
        tracesPerMinute: stats.traceCount / this.windowMinutes(),
        sparkline: this.buildCardSparkline(stats, status),
        // Coloured only when the tenant is unhealthy: a grid of neutral lines with one red line
        // reads at a glance, a grid of coloured lines does not.
        sparklineTone: status === 'degraded' ? 'error' : status === 'slow tail' ? 'warn' : 'default',
      } satisfies TenantCard;
    });

    return this.sortCards(cards, this.sortBy());
  });

  protected readonly hasTenants = computed(() => this.cards().length > 0);

  constructor() {
    this.timeRange.refreshRelativeWindow();

    effect(() => {
      this.timeRange.range();
      untracked(() => this.load());
    });

    // Auto-refresh: re-runs only when the toggle or the selected preset changes. Re-setting the
    // preset re-resolves the window to "now", which the reload effect above already watches — so
    // polling needs no second fetch path. Paused on custom ranges: the window is frozen, so
    // re-querying would return identical data forever.
    effect(() => {
      const enabled = this.autoRefresh();
      const preset = this.preset();
      this.refreshSub?.unsubscribe();
      this.refreshSub = undefined;
      if (enabled && preset !== 'custom') {
        this.refreshSub = interval(recommendedRefreshIntervalMs(preset))
          .subscribe(() => this.timeRange.setPreset(preset));
      }
    });

    effect(() => {
      savePageState(STATE_KEY, {
        sortBy: this.sortBy(),
        autoRefresh: this.autoRefresh(),
      });
    });

    this.destroyRef.onDestroy(() => this.refreshSub?.unsubscribe());
  }

  protected refresh(): void {
    const preset = this.preset();
    // Re-resolve the preset window to "now"; custom ranges just re-query as-is.
    if (preset === 'custom') this.load();
    else this.timeRange.setPreset(preset);
  }

  /**
   * Selecting the tenant before navigating is what makes the card a drill-down rather than a
   * link: the per-tenant pages read `TenantService`, and the shell's picker reappears already
   * showing the tenant the operator clicked.
   */
  protected openTenant(card: TenantCard): void {
    this.tenantService.selectTenant({ id: card.stats.tenantId, name: card.stats.tenantName });
    void this.router.navigate(['/dashboard']);
  }

  protected statusIcon(status: TenantStatus): string {
    switch (status) {
      case 'degraded': return 'error';
      case 'slow tail': return 'timelapse';
      case 'silent': return 'cloud_off';
      case 'unknown': return 'help';
      default: return 'check_circle';
    }
  }

  protected lastSeenLabel(date: Date | null): string {
    if (!date) return 'never';
    const seconds = Math.max(0, Math.round((Date.now() - date.getTime()) / 1000));
    if (seconds < 60) return `${seconds}s ago`;
    if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`;
    if (seconds < 86400) return `${Math.round(seconds / 3600)}h ago`;
    return `${Math.round(seconds / 86400)}d ago`;
  }

  /** Tooltip explaining why a card's p95 is suppressed. */
  protected sampleHint(count: number): string {
    return `Only ${count} ${count === 1 ? 'trace' : 'traces'} in this window — `
      + `a p95 needs at least ${this.thresholds.minSamples} to mean anything.`;
  }

  private windowMinutes(): number {
    const { start, end } = this.timeRange.range();
    return Math.max((end.getTime() - start.getTime()) / 60000, 1);
  }

  /**
   * Volume sparkline geometry. Silent and failed tenants get none — the template draws a flat
   * dashed rule instead, so "no data" never renders as a line sitting on the axis.
   */
  private buildCardSparkline(stats: GlobalTenantStats, status: TenantStatus): SparklineShape | null {
    if (status === 'silent' || status === 'unknown' || stats.buckets.length === 0) return null;

    // `null`, never 0, for empty buckets: a 0 would draw a dip to the axis, which reads as
    // "traffic collapsed" rather than "nothing here".
    const shape = buildSparklineShape(stats.buckets.map((b) => (b.count > 0 ? b.count : null)));
    return shape.segments.length || shape.dots.length ? shape : null;
  }

  private sortCards(cards: TenantCard[], key: SortKey): TenantCard[] {
    const byName = (a: TenantCard, b: TenantCard) =>
      a.stats.tenantName.localeCompare(b.stats.tenantName);

    return [...cards].sort((a, b) => {
      switch (key) {
        case 'name':
          return byName(a, b);
        case 'traffic':
          return b.stats.traceCount - a.stats.traceCount || byName(a, b);
        case 'errors':
          return b.errorRate - a.errorRate || byName(a, b);
        case 'latency':
          return b.stats.p95Ms - a.stats.p95Ms || byName(a, b);
        default:
          // Worst-first, then busiest — on a grid of many tenants the ordering is the feature.
          return TENANT_STATUS_ORDER[a.status] - TENANT_STATUS_ORDER[b.status]
            || b.stats.traceCount - a.stats.traceCount
            || byName(a, b);
      }
    });
  }

  private load(): void {
    const { start, end } = this.timeRange.range();
    this.loading.set(true);
    this.api
      .getOverview(start, end)
      .pipe(catchError(() => of({ tenants: [] })))
      .subscribe((result) => {
        this.tenants.set(result.tenants);
        this.loading.set(false);
      });
  }
}
