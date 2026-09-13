import { Component, DestroyRef, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { Subscription, forkJoin, interval, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { MatCardModule } from '@angular/material/card';
import { MatTableModule } from '@angular/material/table';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatChipsModule } from '@angular/material/chips';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { NgApexchartsModule } from 'ng-apexcharts';
import type { ApexOptions } from 'ng-apexcharts';

import { TracesApiService } from '../../core/services/api/traces-api.service';
import { LogsApiService } from '../../core/services/api/logs-api.service';
import { MetricsApiService } from '../../core/services/api/metrics-api.service';
import { TimeRangeService, recommendedRefreshIntervalMs } from '../../core/services/time-range.service';
import { ThemeService } from '../../core/services/theme.service';
import { ServiceStats, TraceInfo } from '../../core/models/trace.models';
import { StatCardComponent } from '../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { PageHeaderComponent } from '../../shared/components/page-header/page-header.component';
import { ServiceHealthTableComponent } from './service-health-table/service-health-table.component';
import {
  TimeBucket, LogBucket, buildSparklineOptions,
  formatDuration, parseDotnetTimespan, timeRangeZoom,
} from '../../shared/utils/chart.utils';
import { loadPageState, savePageState } from '../../shared/utils/page-state';

const STATE_KEY = 'state.dashboard';

/**
 * Error-rate KPI thresholds. Deliberately separate from any alert rule: alert rules are
 * per-tenant, per-rule-type, and often scoped to one service, so there is no single sensible way
 * to fold an arbitrary set of them into one global card. Alerts own "is this a violation"; this
 * card owns "does this look off at a glance" — different jobs, different thresholds.
 */
const ERROR_RATE_WARN = 0.01;
const ERROR_RATE_ERROR = 0.05;

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, PercentPipe, RouterLink,
    MatCardModule, MatTableModule, MatIconModule,
    MatButtonModule, MatProgressBarModule, MatChipsModule,
    MatSlideToggleModule, MatTooltipModule, MatFormFieldModule, MatSelectModule,
    NgApexchartsModule, StatCardComponent, EmptyStateComponent, ServiceHealthTableComponent,
    PageHeaderComponent,
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent {
  private readonly tracesApi = inject(TracesApiService);
  private readonly logsApi = inject(LogsApiService);
  private readonly metricsApi = inject(MetricsApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly theme = inject(ThemeService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  private readonly saved = loadPageState(STATE_KEY, {
    selectedService: '',
    autoRefresh: false,
  });

  protected loading = signal(true);
  protected autoRefresh = signal(this.saved.autoRefresh);
  protected readonly preset = computed(() => this.timeRange.range().preset);
  private refreshSub?: Subscription;
  /** Bounded (limit: 500) sample used for the recent-errors/slowest-traces tables. */
  protected traces = signal<TraceInfo[]>([]);
  protected availableServices = signal<string[]>([]);
  protected selectedService = signal(this.saved.selectedService);
  /** True (unbounded) volume histogram — backs the chart and the trace-count/error-rate stat cards. */
  private traceHistogram = signal<TimeBucket[]>([]);
  /**
   * Severity histogram — backs the log stat cards (full severity breakdown lives on the Logs
   * page). Counts come from here rather than a raw `GET /api/logs` fetch: that endpoint is
   * unbounded (no limit parameter), so the page was pulling every log record in the window over
   * the wire just to read `.length`.
   */
  private logHistogram = signal<LogBucket[]>([]);
  /** Per-service RED stats — comes from the same `/overview` fetch as `traceHistogram`, not a separate request. */
  protected serviceStats = signal<ServiceStats[]>([]);

  protected totalTraces = computed(() => this.traceHistogram().reduce((a, b) => a + b.count, 0));
  protected errorTraces = computed(() => this.traceHistogram().reduce((a, b) => a + b.errorCount, 0));
  protected errorRate = computed(() =>
    this.totalTraces() > 0 ? this.errorTraces() / this.totalTraces() : 0
  );
  /** 'default' | 'warn' | 'error' coloring for the Error Rate card — thresholds above. */
  protected errorRateColor = computed<'default' | 'warn' | 'error'>(() => {
    const rate = this.errorRate();
    if (rate >= ERROR_RATE_ERROR) return 'error';
    if (rate >= ERROR_RATE_WARN) return 'warn';
    return 'default';
  });
  protected serviceCount = computed(() => this.availableServices().length);
  protected logTotal = computed(() =>
    this.logHistogram().reduce(
      (a, b) => a + b.trace + b.debug + b.info + b.warn + b.error + b.fatal, 0)
  );
  /** Error + fatal only — the log signal worth surfacing next to the trace RED metrics. */
  protected logErrorCount = computed(() =>
    this.logHistogram().reduce((a, b) => a + b.error + b.fatal, 0)
  );
  protected recentErrors = computed(() =>
    this.traces().filter((t) => t.hasErrors).slice(0, 5)
  );
  protected slowTraces = computed(() =>
    [...this.traces()]
      .filter((t) => parseDotnetTimespan(t.traceDuration) > 500)
      .sort((a, b) => parseDotnetTimespan(b.traceDuration) - parseDotnetTimespan(a.traceDuration))
      .slice(0, 5)
  );

  // ---------------------------------------------------------------------------------------------
  // RED KPIs (Rate, Errors, Duration) — all derived from `traceHistogram`/`logHistogram`, which
  // the page already fetches, so none of this adds a request.
  // ---------------------------------------------------------------------------------------------

  private windowSeconds = computed(() => {
    const { start, end } = this.timeRange.range();
    return Math.max((end.getTime() - start.getTime()) / 1000, 1);
  });
  protected tracesPerSecond = computed(() => this.totalTraces() / this.windowSeconds());
  private sumDurationMsTotal = computed(() => this.traceHistogram().reduce((a, b) => a + b.sumDurationMs, 0));
  protected avgDurationMs = computed(() =>
    this.totalTraces() > 0 ? this.sumDurationMsTotal() / this.totalTraces() : 0
  );

  /** Per-bucket trace counts — 0 is a real value here (an idle bucket genuinely saw no traces). */
  protected countSeries = computed<(number | null)[]>(() => this.traceHistogram().map((b) => b.count));
  /**
   * Per-bucket avg duration / error rate. `null` (not 0) for an empty bucket: with no traces,
   * "average duration" and "error rate" are undefined, not zero — plotting 0 would read as
   * "responses briefly became instant" / "errors briefly vanished" instead of "no data here".
   */
  protected avgDurationSeries = computed<(number | null)[]>(() =>
    this.traceHistogram().map((b) => (b.count > 0 ? b.sumDurationMs / b.count : null))
  );
  protected errorRateSeries = computed<(number | null)[]>(() =>
    this.traceHistogram().map((b) => (b.count > 0 ? b.errorCount / b.count : null))
  );
  /** Per-bucket error+fatal log count — 0 is real data (no error logs in that bucket). */
  protected logErrorSeries = computed<(number | null)[]>(() =>
    this.logHistogram().map((b) => b.error + b.fatal)
  );

  protected tracesSparkline = computed(() =>
    buildSparklineOptions(this.countSeries(), this.theme.isDark(), '#2196f3')
  );
  protected durationSparkline = computed(() =>
    buildSparklineOptions(this.avgDurationSeries(), this.theme.isDark(), '#2196f3')
  );
  protected errorRateSparkline = computed(() =>
    buildSparklineOptions(this.errorRateSeries(), this.theme.isDark(), '#f44336')
  );
  protected logErrorSparkline = computed(() =>
    buildSparklineOptions(this.logErrorSeries(), this.theme.isDark(), '#f44336')
  );

  protected traceChartOptions = signal<ApexOptions>({});
  protected latencyChartOptions = signal<ApexOptions>({});

  constructor() {
    // Slide relative preset windows to "now" on (re)entry so navigating back refreshes.
    this.timeRange.refreshRelativeWindow();

    effect(() => {
      this.timeRange.range();
      this.selectedService();
      untracked(() => this.load());
    });

    // Auto-refresh: re-runs only when the toggle or the selected preset changes.
    // Paused on custom ranges (frozen window shouldn't re-query).
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
        selectedService: this.selectedService(),
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

  private load(): void {
    this.loading.set(true);
    const { start, end } = this.timeRange.range();
    const svc = this.selectedService();

    forkJoin({
      traces:     this.tracesApi.getTraces({ start, end, limit: 500, service: svc || undefined }),
      // Swaps the plain histogram for the overview (same buckets, plus per-service RED stats) —
      // one backend scan grouped two ways, not an additional request. See Phase 5 of the plan.
      overview:   this.tracesApi.getTraceOverview({ start, end, service: svc || undefined }),
      logHist:    this.logsApi.getLogHistogram({ start, end, service: svc || undefined }),
      traceSvcs:  this.tracesApi.getServices(start, end).pipe(catchError(() => of([]))),
      logSvcs:    this.logsApi.getServices(start, end).pipe(catchError(() => of([]))),
      metricSvcs: this.metricsApi.getServices(start, end).pipe(catchError(() => of([]))),
    }).subscribe({
      next: ({ traces, overview, logHist, traceSvcs, logSvcs, metricSvcs }) => {
        this.traces.set(traces);
        this.traceHistogram.set(overview.buckets);
        this.serviceStats.set(overview.services);
        this.logHistogram.set(logHist);
        const services = [...new Set([...traceSvcs, ...logSvcs, ...metricSvcs])].sort();
        if (services.length > 0) this.availableServices.set(services);
        this.buildCharts(overview.buckets);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  /** Service Health row click: select that service, or clear the filter if it's already active. */
  protected onServiceRowClick(service: string): void {
    this.selectedService.set(this.selectedService() === service ? '' : service);
  }

  private buildCharts(traceBuckets: TimeBucket[]): void {
    const isDark = this.theme.isDark();
    const timestamps = traceBuckets.map((b) => b.timestamp.getTime());

    const zoom = timeRangeZoom((from, to) => this.timeRange.setCustom(from, to));

    this.traceChartOptions.set({
      // Fixed pixel height, deliberately not tied to the card's (fluid) width: `.dashboard-grid`
      // stretches this card's width to fill its row, and height must not follow along with it.
      // (A prior `height: '100%'` + CSS `aspect-ratio` container attempted responsive height too,
      // but ApexCharts' percentage-height resolution reads `this.el.parentNode` — the `apx-chart`
      // host itself, which ng-apexcharts leaves entirely unstyled — not the aspect-ratio box, so
      // it never actually tracked the container; that's what left a gap under the chart.)
      chart: { type: 'area', height: 280, toolbar: { show: false }, background: 'transparent', ...zoom },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: [
        { name: 'Total', data: traceBuckets.map((b, i) => [timestamps[i], b.count]) },
        { name: 'Errors', data: traceBuckets.map((b, i) => [timestamps[i], b.errorCount]) },
      ],
      xaxis: { type: 'datetime', labels: { datetimeUTC: false } },
      colors: ['#2196f3', '#f44336'],
      stroke: { curve: 'smooth', width: 2 },
      fill: { opacity: 0.2 },
      legend: { position: 'top' },
      dataLabels: { enabled: false },
    });

    // Empty buckets plot as `null` (a gap), not 0 — see avgDurationSeries above for why.
    this.latencyChartOptions.set({
      chart: { type: 'line', height: 280, toolbar: { show: false }, background: 'transparent', ...zoom },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: [
        {
          name: 'Avg',
          data: traceBuckets.map((b, i) => [timestamps[i], b.count > 0 ? b.sumDurationMs / b.count : null]),
        },
        {
          name: 'p50',
          data: traceBuckets.map((b, i) => [timestamps[i], b.count > 0 ? b.p50Ms : null]),
        },
        {
          name: 'p95',
          data: traceBuckets.map((b, i) => [timestamps[i], b.count > 0 ? b.p95Ms : null]),
        },
        {
          name: 'p99',
          data: traceBuckets.map((b, i) => [timestamps[i], b.count > 0 ? b.p99Ms : null]),
        },
      ],
      xaxis: { type: 'datetime', labels: { datetimeUTC: false } },
      yaxis: { labels: { formatter: (v: number) => formatDuration(v) } },
      // Avg neutral (matches "Total" in Trace Volume); p50/p95/p99 a deliberate green→orange→red
      // severity gradient, reusing colors already established elsewhere on this dashboard (the
      // stat-card `success` color, the Error-Rate `warn` tier, and "Errors" in Trace Volume).
      colors: ['#2196f3', '#4caf50', '#ff9800', '#f44336'],
      stroke: { curve: 'smooth', width: 2 },
      // Same stray-marker fix as the sparklines (see buildSparklineOptions): every series here
      // carries `null`s, so ApexCharts emits one 0.1px-radius "virtual point" per series pinned to
      // the bottom-left of the plot, and their default 2px `#fff` stroke was the only visible part
      // — four white dots stacked on the x-axis. Zeroing the stroke hides them.
      //
      // That parked dot and the dot that tracks the crosshair on hover are the SAME four elements
      // (ApexCharts just moves and enlarges them to r=6), so this necessarily drops the hover dot's
      // white outline ring too — it now renders as a solid circle in the series color, still
      // clearly legible against the plot. The legend swatches are separate elements
      // (`apexcharts-legend-marker`) and keep their ring.
      markers: { strokeWidth: 0 },
      legend: { position: 'top' },
      dataLabels: { enabled: false },
      tooltip: { y: { formatter: (v: number) => formatDuration(v) } },
    });
  }

  protected navigateToTrace(traceId: string): void {
    this.router.navigate(['/traces', traceId]);
  }

  protected durationMs(trace: TraceInfo): number {
    return parseDotnetTimespan(trace.traceDuration);
  }

  protected formatDuration = formatDuration;
}
