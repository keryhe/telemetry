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
import { StatCardComponent } from '../../shared/components/stat-card/stat-card.component';
import { formatDuration } from '../../shared/utils/chart.utils';
import { loadPageState, savePageState } from '../../shared/utils/page-state';
import {
  HEALTH_THRESHOLDS_TOKEN, HealthColor, HealthReason, TENANT_STATUS_ORDER, TenantStatus,
  classifyErrorRate, classifyLogErrorRate, classifyP95, hasEnoughSamples,
  tenantHealth,
} from '../../shared/config/health-thresholds';

function formatPercent(fraction: number): string {
  const pct = fraction * 100;
  return `${Number.isInteger(pct) ? pct : pct.toFixed(1)}%`;
}

/** A threshold as a person would write it: 500 ms, 1.5 s (no trailing zeros, unlike a measured value). */
function formatThresholdMs(ms: number): string {
  return ms >= 1000 ? `${+(ms / 1000).toFixed(2)} s` : `${+ms.toFixed(1)} ms`;
}

function formatAge(ms: number): string {
  const minutes = Math.round(ms / 60000);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.round(minutes / 60);
  return hours < 48 ? `${hours}h` : `${Math.round(hours / 24)}d`;
}

const STATE_KEY = 'state.globalDashboard';

type SortKey = 'health' | 'name' | 'traffic' | 'errors' | 'latency';

/**
 * What a stat card filters the grid by. `no data` groups `silent` and `unknown`: both mean the
 * dashboard cannot say anything about the tenant, and the second is a collection failure rather
 * than a state worth its own count.
 */
type StatusGroup = 'degraded' | 'warning' | 'slow tail' | 'healthy' | 'no data';

function statusGroup(status: TenantStatus): StatusGroup {
  return status === 'silent' || status === 'unknown' ? 'no data' : status;
}

/** One status stat card above the grid. */
interface StatusSummary {
  group: StatusGroup;
  label: string;
  icon: string;
  count: number;
  /** The icon's colour, so the stat card matches the status colours the cards use. */
  iconColor: string;
  color: 'default' | 'error' | 'warn' | 'success';
}

/** The one place a status gets its icon: card, stat card and legend all read this. */
const STATUS_ICON: Record<TenantStatus, string> = {
  'degraded': 'error',
  'warning': 'warning',
  'slow tail': 'timelapse',
  'healthy': 'check_circle',
  'silent': 'cloud_off',
  'unknown': 'help',
};

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
  tracesPerMinute: number;
  /** The worst matched condition, as text. Null for a healthy card. */
  primaryReason: string | null;
  /** Every other matched condition, as chips. */
  extraReasons: string[];
}

@Component({
  selector: 'app-global-dashboard',
  standalone: true,
  imports: [
    DecimalPipe, NgClass, PercentPipe,
    StatCardComponent, MatCardModule, MatIconModule, MatButtonModule, MatProgressBarModule,
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
  /**
   * Deliberately not part of the saved page state: a filter left on from a previous visit would
   * hide problem tenants without anyone having asked for that today.
   */
  protected statusFilter = signal<StatusGroup | null>(null);
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

  /** One card model per tenant, already classified (unfiltered, unsorted). */
  private readonly allCards = computed<TenantCard[]>(() => {
    const t = this.thresholds;

    return this.tenants().map((stats) => {
      const errorRate = stats.traceCount > 0 ? stats.errorCount / stats.traceCount : 0;
      const { status, reasons } = tenantHealth(
        {
          traceCount: stats.traceCount,
          errorCount: stats.errorCount,
          p95Ms: stats.p95Ms,
          p99Ms: stats.p99Ms,
          logCount: stats.logCount,
          logErrorCount: stats.logErrorCount,
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
        tracesPerMinute: stats.traceCount / this.windowMinutes(),
        primaryReason: reasons.length ? this.reasonText(reasons[0]) : null,
        extraReasons: reasons.slice(1).map((r) => this.reasonText(r)),
      } satisfies TenantCard;
    });
  });

  /** The cards the grid shows: filtered by the selected stat card, then sorted. */
  protected readonly cards = computed<TenantCard[]>(() => {
    const filter = this.statusFilter();
    const visible = filter
      ? this.allCards().filter((c) => statusGroup(c.status) === filter)
      : this.allCards();
    return this.sortCards(visible, this.sortBy());
  });

  /**
   * Counts come from the same classified cards the grid renders, so a stat card and the grid
   * under it cannot disagree. "No data" appears only when something is in it.
   */
  protected readonly statusSummaries = computed<StatusSummary[]>(() => {
    const counts = new Map<StatusGroup, number>();
    for (const card of this.allCards()) {
      const group = statusGroup(card.status);
      counts.set(group, (counts.get(group) ?? 0) + 1);
    }

    const make = (
      group: StatusGroup, label: string, status: TenantStatus,
      problem: 'error' | 'warn' | null, color: string,
    ): StatusSummary => {
      const count = counts.get(group) ?? 0;
      // Colour only a count that is above zero: a red "Degraded 0" is noise.
      const lit = count > 0;
      return {
        group, label, count, icon: STATUS_ICON[status],
        color: lit ? (problem ?? (group === 'healthy' ? 'success' : 'default')) : 'default',
        iconColor: lit ? color : 'var(--mat-sys-on-surface-variant)',
      };
    };

    const summaries = [
      make('degraded', 'Degraded', 'degraded', 'error', 'var(--status-degraded)'),
      make('warning', 'Warning', 'warning', 'warn', 'var(--status-warning)'),
      make('slow tail', 'Slow tail', 'slow tail', null, 'var(--status-slow-tail)'),
      make('healthy', 'Healthy', 'healthy', null, 'var(--status-healthy)'),
    ];
    if ((counts.get('no data') ?? 0) > 0) {
      summaries.push(make('no data', 'No data', 'silent', null, 'var(--status-silent)'));
    }
    return summaries;
  });

  protected readonly tenantTotal = computed(() => this.allCards().length);

  // Unfiltered: a filter that matches nothing is not "no tenants".
  protected readonly hasTenants = computed(() => this.allCards().length > 0);

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
    return STATUS_ICON[status];
  }

  /** Click again on the selected card to clear the filter. */
  protected toggleStatusFilter(group: StatusGroup): void {
    this.statusFilter.update((current) => (current === group ? null : group));
  }

  /**
   * Rows for the legend under the grid: every status with the rule that gives it, read from the
   * same thresholds the classifier uses so the two cannot describe different numbers.
   */
  protected readonly legend = computed(() => {
    const t = this.thresholds;
    const pct = (v: number) => formatPercent(v);
    const minutes = Math.round(t.silentAfterMs / 60000);
    return [
      {
        status: 'degraded' as TenantStatus,
        rule: `error rate at ${pct(t.errorRate.critical)} or more, or p95 at ${formatThresholdMs(t.p95Ms.critical)} or more`,
      },
      {
        status: 'warning' as TenantStatus,
        rule: `error rate at ${pct(t.errorRate.warn)} or more, p95 at ${formatThresholdMs(t.p95Ms.warn)} or more, `
          + `or log errors at ${pct(t.logErrorRate.critical)} or more of logs`,
      },
      {
        status: 'slow tail' as TenantStatus,
        rule: `p99 more than ${t.tailRatio}× the p95`,
      },
      {
        status: 'healthy' as TenantStatus,
        rule: 'none of the above',
      },
      {
        status: 'silent' as TenantStatus,
        rule: `no traces in the window and none seen for ${minutes} ${minutes === 1 ? 'minute' : 'minutes'}`,
      },
      {
        status: 'unknown' as TenantStatus,
        rule: 'the tenant\'s stats could not be loaded',
      },
    ];
  });

  protected readonly legendNote = computed(() => {
    const t = this.thresholds;
    return `Latency rules (p95, slow tail) need at least ${t.minSamples} traces in the window. `
      + `Log errors raise a tenant's status only at ${formatPercent(t.logErrorRate.critical)} or more; `
      + `from ${formatPercent(t.logErrorRate.warn)} they only colour the log count.`;
  });

  private reasonText(r: HealthReason): string {
    switch (r.kind) {
      case 'failed':
        return 'stats could not be loaded';
      case 'silent':
        return r.value !== undefined && isFinite(r.value)
          ? `no data for ${formatAge(r.value)}`
          : 'no data seen';
      case 'errorRate':
        return `error rate ${formatPercent(r.value!)} (≥ ${formatPercent(r.threshold!)})`;
      case 'p95':
        return `p95 ${formatDuration(r.value!)} (≥ ${formatThresholdMs(r.threshold!)})`;
      case 'logErrors':
        return `log errors ${formatPercent(r.value!)} (≥ ${formatPercent(r.threshold!)})`;
      case 'slowTail':
        return `p99 ${r.value!.toFixed(1)}× p95 (> ${r.threshold}×)`;
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
