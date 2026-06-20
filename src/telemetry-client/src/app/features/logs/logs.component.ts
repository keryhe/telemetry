import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, KeyValuePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { NgApexchartsModule } from 'ng-apexcharts';
import type { ApexOptions } from 'ng-apexcharts';
import { FormsModule } from '@angular/forms';

import { LogsApiService } from '../../core/services/api/logs-api.service';
import { TimeRangeService } from '../../core/services/time-range.service';
import { ThemeService } from '../../core/services/theme.service';
import { LogRecord, getSeverityLabel, getSeverityColor, SEVERITY_LABELS, getServiceName, getTimestamp } from '../../core/models/log.models';
import { SeverityBadgeComponent } from '../../shared/components/severity-badge/severity-badge.component';
import { StatCardComponent } from '../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { bucketLogs } from '../../shared/utils/chart.utils';

const BUCKET_COUNT = 30;

@Component({
  selector: 'app-logs',
  standalone: true,
  imports: [
    DatePipe, KeyValuePipe, FormsModule, RouterLink,
    MatCardModule, MatTableModule, MatExpansionModule, MatIconModule,
    MatFormFieldModule, MatInputModule, MatSelectModule, MatProgressBarModule,
    MatButtonModule, NgApexchartsModule,
    SeverityBadgeComponent, StatCardComponent, EmptyStateComponent,
  ],
  templateUrl: './logs.component.html',
  styleUrl: './logs.component.scss',
})
export class LogsComponent implements OnInit {
  private readonly api = inject(LogsApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly theme = inject(ThemeService);
  private readonly route = inject(ActivatedRoute);

  protected loading = signal(true);
  protected logs = signal<LogRecord[]>([]);
  protected searchText = signal('');
  protected selectedService = signal('');
  protected selectedSeverity = signal(-1);
  protected traceIdFilter = signal('');
  protected expandedIdx = signal<number | null>(null);

  protected services = computed(() =>
    [...new Set(this.logs().map((l) => getServiceName(l)))]
  );

  protected severityOptions = [
    { num: 9, label: 'Info' },
    { num: 13, label: 'Warn' },
    { num: 17, label: 'Error' },
    { num: 21, label: 'Fatal' },
  ];

  protected filtered = computed(() => {
    let result = this.logs();
    const text = this.searchText().toLowerCase();
    const svc = this.selectedService();
    const sev = this.selectedSeverity();

    if (text) result = result.filter((l) =>
      (l.bodyValue?.toLowerCase().includes(text)) ||
      (l.traceIdHex?.toLowerCase().includes(text))
    );
    if (svc) result = result.filter((l) => getServiceName(l) === svc);
    if (sev >= 0) result = result.filter((l) => (l.severityNumber ?? 0) >= sev);

    return result;
  });

  protected totalCount = computed(() => this.logs().length);
  protected errorCount = computed(() => this.logs().filter((l) => (l.severityNumber ?? 0) >= 17).length);
  protected warnCount = computed(() => this.logs().filter((l) => {
    const s = l.severityNumber ?? 0; return s >= 13 && s < 17;
  }).length);

  protected chartOptions = signal<ApexOptions>({});

  protected readonly displayedColumns = ['time', 'severity', 'service', 'message'];
  protected readonly getSeverityLabel = getSeverityLabel;
  protected readonly getSeverityColor = getSeverityColor;
  protected readonly getServiceName = getServiceName;
  protected readonly getTimestamp = getTimestamp;

  ngOnInit(): void {
    const traceId = this.route.snapshot.queryParamMap.get('traceId');
    if (traceId) {
      this.traceIdFilter.set(traceId);
      this.loadByTrace(traceId);
    } else {
      this.load();
    }
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

  private buildChart(): void {
    const { start, end } = this.timeRange.range();
    const isDark = this.theme.isDark();
    const buckets = bucketLogs(this.logs(), start, end, BUCKET_COUNT);

    this.chartOptions.set({
      chart: { type: 'bar', height: 100, toolbar: { show: false }, stacked: true, background: 'transparent' },
      theme: { mode: isDark ? 'dark' : 'light' },
      series: [
        { name: 'Error', data: buckets.map((b) => b.errors) },
        { name: 'Warn', data: buckets.map((b) => b.warnings) },
        { name: 'Info', data: buckets.map((b) => b.info) },
      ],
      colors: ['#f44336', '#ff9800', '#2196f3'],
      xaxis: {
        categories: buckets.map((b) => b.time.toISOString()),
        type: 'datetime', labels: { show: false }, axisBorder: { show: false }, axisTicks: { show: false },
      },
      dataLabels: { enabled: false },
      legend: { show: false },
      plotOptions: { bar: { columnWidth: '90%' } },
      grid: { show: false },
    });
  }

  protected toggleRow(idx: number): void {
    this.expandedIdx.update((cur) => (cur === idx ? null : idx));
  }

  protected clearTrace(): void {
    this.traceIdFilter.set('');
    this.load();
  }
}
