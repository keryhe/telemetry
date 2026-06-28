import { Component, DestroyRef, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { Router } from '@angular/router';
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
import { TraceInfo } from '../../core/models/trace.models';
import { LogRecord, getServiceName } from '../../core/models/log.models';
import { StatCardComponent } from '../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { bucketTraces, bucketLogs, buildLogSeriesOptions, formatDuration, parseDotnetTimespan } from '../../shared/utils/chart.utils';
import { loadPageState, savePageState } from '../../shared/utils/page-state';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, PercentPipe,
    MatCardModule, MatTableModule, MatIconModule,
    MatButtonModule, MatProgressBarModule, MatChipsModule,
    MatSlideToggleModule, MatTooltipModule, MatFormFieldModule, MatSelectModule,
    NgApexchartsModule, StatCardComponent, EmptyStateComponent,
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
const STATE_KEY = 'state.dashboard';

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
  protected traces = signal<TraceInfo[]>([]);
  protected logs = signal<LogRecord[]>([]);
  protected availableServices = signal<string[]>([]);
  protected selectedService = signal(this.saved.selectedService);

  protected totalTraces = computed(() => this.traces().length);
  protected errorTraces = computed(() => this.traces().filter((t) => t.hasErrors).length);
  protected errorRate = computed(() =>
    this.totalTraces() > 0 ? this.errorTraces() / this.totalTraces() : 0
  );
  protected serviceCount = computed(() => this.availableServices().length);
  protected recentErrors = computed(() =>
    this.traces().filter((t) => t.hasErrors).slice(0, 5)
  );
  protected slowTraces = computed(() =>
    [...this.traces()]
      .filter((t) => parseDotnetTimespan(t.traceDuration) > 500)
      .sort((a, b) => parseDotnetTimespan(b.traceDuration) - parseDotnetTimespan(a.traceDuration))
      .slice(0, 5)
  );

  protected traceChartOptions = signal<ApexOptions>({});
  protected logChartOptions = signal<ApexOptions>({});

  constructor() {
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
      logs:       this.logsApi.getLogs(start, end),
      traceSvcs:  this.tracesApi.getServices(start, end).pipe(catchError(() => of([]))),
      logSvcs:    this.logsApi.getServices(start, end).pipe(catchError(() => of([]))),
      metricSvcs: this.metricsApi.getServices(start, end).pipe(catchError(() => of([]))),
    }).subscribe({
      next: ({ traces, logs, traceSvcs, logSvcs, metricSvcs }) => {
        this.traces.set(traces);
        this.logs.set(svc ? logs.filter((l) => getServiceName(l) === svc) : logs);
        const services = [...new Set([...traceSvcs, ...logSvcs, ...metricSvcs])].sort();
        if (services.length > 0) this.availableServices.set(services);
        this.buildCharts(start, end);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private buildCharts(start: Date, end: Date): void {
    const isDark = this.theme.isDark();
    const buckets = bucketTraces(this.traces(), start, end);
    const timestamps = buckets.map((b) => b.timestamp.getTime());

    this.traceChartOptions.set({
      chart: { type: 'area', height: 220, toolbar: { show: false }, background: 'transparent' },
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

    const logBuckets = bucketLogs(this.logs(), start, end);
    this.logChartOptions.set(buildLogSeriesOptions(logBuckets, isDark, 220));
  }

  protected navigateToTrace(traceId: string): void {
    this.router.navigate(['/traces', traceId]);
  }

  protected durationMs(trace: TraceInfo): number {
    return parseDotnetTimespan(trace.traceDuration);
  }

  protected formatDuration = formatDuration;
}
