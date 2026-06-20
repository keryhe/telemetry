import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { Router } from '@angular/router';
import { forkJoin } from 'rxjs';
import { MatCardModule } from '@angular/material/card';
import { MatTableModule } from '@angular/material/table';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatChipsModule } from '@angular/material/chips';
import { NgApexchartsModule } from 'ng-apexcharts';
import type { ApexOptions } from 'ng-apexcharts';

import { TracesApiService } from '../../core/services/api/traces-api.service';
import { LogsApiService } from '../../core/services/api/logs-api.service';
import { MetricsApiService } from '../../core/services/api/metrics-api.service';
import { TimeRangeService } from '../../core/services/time-range.service';
import { ThemeService } from '../../core/services/theme.service';
import { TraceInfo } from '../../core/models/trace.models';
import { LogRecord } from '../../core/models/log.models';
import { ServiceMetricSummary } from '../../core/models/metric.models';
import { StatCardComponent } from '../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { bucketTraces, bucketLogs, formatDuration, parseDotnetTimespan } from '../../shared/utils/chart.utils';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, PercentPipe,
    MatCardModule, MatTableModule, MatIconModule,
    MatButtonModule, MatProgressBarModule, MatChipsModule,
    NgApexchartsModule, StatCardComponent, EmptyStateComponent,
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent implements OnInit {
  private readonly tracesApi = inject(TracesApiService);
  private readonly logsApi = inject(LogsApiService);
  private readonly metricsApi = inject(MetricsApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly theme = inject(ThemeService);
  private readonly router = inject(Router);

  protected loading = signal(true);
  protected traces = signal<TraceInfo[]>([]);
  protected logs = signal<LogRecord[]>([]);
  protected metricSummaries = signal<ServiceMetricSummary[]>([]);

  protected totalTraces = computed(() => this.traces().length);
  protected errorTraces = computed(() => this.traces().filter((t) => t.hasErrors).length);
  protected errorRate = computed(() =>
    this.totalTraces() > 0 ? this.errorTraces() / this.totalTraces() : 0
  );
  protected serviceCount = computed(() => this.metricSummaries().length);
  protected recentErrors = computed(() =>
    this.traces().filter((t) => t.hasErrors).slice(0, 5)
  );
  protected slowTraces = computed(() =>
    [...this.traces()]
      .sort((a, b) => parseDotnetTimespan(b.traceDuration) - parseDotnetTimespan(a.traceDuration))
      .slice(0, 5)
  );

  protected traceChartOptions = signal<ApexOptions>({});
  protected logChartOptions = signal<ApexOptions>({});

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    const { start, end } = this.timeRange.range();

    forkJoin({
      traces: this.tracesApi.getTraces({ start, end, limit: 500 }),
      logs: this.logsApi.getLogs(start, end),
      summaries: this.metricsApi.getSummaries(),
    }).subscribe({
      next: ({ traces, logs, summaries }) => {
        this.traces.set(traces);
        this.logs.set(logs);
        this.metricSummaries.set(summaries);
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
      xaxis: { type: 'datetime' },
      colors: ['#2196f3', '#f44336'],
      stroke: { curve: 'smooth', width: 2 },
      fill: { opacity: 0.2 },
      legend: { position: 'top' },
      dataLabels: { enabled: false },
    });

    const logBuckets = bucketLogs(this.logs(), start, end);

    this.logChartOptions.set({
      chart: { type: 'bar', height: 220, toolbar: { show: false }, background: 'transparent', stacked: true },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: [
        { name: 'Error', data: logBuckets.map((b) => b.errors) },
        { name: 'Warn', data: logBuckets.map((b) => b.warnings) },
        { name: 'Info', data: logBuckets.map((b) => b.info) },
      ],
      colors: ['#f44336', '#ff9800', '#4caf50'],
      xaxis: { categories: logBuckets.map((b) => b.time.getTime()), type: 'datetime' },
      legend: { position: 'top' },
      dataLabels: { enabled: false },
      plotOptions: { bar: { columnWidth: '80%' } },
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
