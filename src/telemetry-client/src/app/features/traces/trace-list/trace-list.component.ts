import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe, SlicePipe } from '@angular/common';
import { Router } from '@angular/router';
import { forkJoin } from 'rxjs';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatChipsModule } from '@angular/material/chips';
import { FormsModule } from '@angular/forms';
import { NgxGraphModule } from '@swimlane/ngx-graph';

import { TracesApiService } from '../../../core/services/api/traces-api.service';
import { TimeRangeService } from '../../../core/services/time-range.service';
import { TraceInfo, ServiceDependency } from '../../../core/models/trace.models';
import { StatCardComponent } from '../../../shared/components/stat-card/stat-card.component';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { formatDuration, parseDotnetTimespan } from '../../../shared/utils/chart.utils';

interface GraphNode { id: string; label: string; }
interface GraphLink { id: string; source: string; target: string; label: string; }

@Component({
  selector: 'app-trace-list',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, SlicePipe, FormsModule,
    MatCardModule, MatTableModule, MatTabsModule, MatIconModule,
    MatButtonToggleModule, MatSelectModule, MatFormFieldModule,
    MatInputModule, MatProgressBarModule, MatChipsModule,
    NgxGraphModule, StatCardComponent, EmptyStateComponent,
  ],
  templateUrl: './trace-list.component.html',
  styleUrl: './trace-list.component.scss',
})
export class TraceListComponent implements OnInit {
  private readonly api = inject(TracesApiService);
  private readonly timeRange = inject(TimeRangeService);
  private readonly router = inject(Router);

  protected loading = signal(true);
  protected traces = signal<TraceInfo[]>([]);
  protected services = signal<string[]>([]);
  protected dependencies = signal<ServiceDependency[]>([]);
  protected operationCounts = signal<Record<string, number>>({});
  protected latencies = signal<Record<string, number>>({});

  protected filterMode = signal<'all' | 'errors' | 'slow'>('all');
  protected selectedService = signal<string>('');
  protected searchText = signal('');
  protected minDurationMs = signal(500);
  protected analyticsService = signal('');

  protected filteredTraces = computed(() => {
    const text = this.searchText().toLowerCase();
    if (!text) return this.traces();
    return this.traces().filter(
      (t) =>
        t.traceIdHex.toLowerCase().includes(text) ||
        t.serviceName?.toLowerCase().includes(text) ||
        t.rootOperationName?.toLowerCase().includes(text)
    );
  });

  protected totalTraces = computed(() => this.traces().length);
  protected errorCount = computed(() => this.traces().filter((t) => t.hasErrors).length);
  protected errorRate = computed(() =>
    this.totalTraces() > 0 ? ((this.errorCount() / this.totalTraces()) * 100).toFixed(1) + '%' : '0%'
  );
  protected avgDuration = computed(() => {
    const ms = this.traces().map((t) => parseDotnetTimespan(t.traceDuration));
    return ms.length > 0 ? formatDuration(ms.reduce((a, b) => a + b, 0) / ms.length) : '—';
  });

  protected graphNodes = computed<GraphNode[]>(() =>
    [...new Set([
      ...this.dependencies().map((d) => d.parentService),
      ...this.dependencies().map((d) => d.childService),
    ])].map((s) => ({ id: s, label: s }))
  );

  protected graphLinks = computed<GraphLink[]>(() =>
    this.dependencies().map((d, i) => ({
      id: `link-${i}`,
      source: d.parentService,
      target: d.childService,
      label: `${d.callCount}`,
    }))
  );

  protected operationRows = computed(() =>
    Object.entries(this.operationCounts())
      .map(([op, count]) => ({ op, count, avgMs: this.latencies()[op] ?? 0 }))
      .sort((a, b) => b.count - a.count)
  );

  protected readonly displayedColumns = ['traceId', 'service', 'operation', 'duration', 'status', 'time'];
  protected readonly formatDuration = formatDuration;
  protected readonly parseDuration = parseDotnetTimespan;

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    const { start, end } = this.timeRange.range();

    forkJoin({
      traces: this.api.getTraces({
        start, end, limit: 200,
        mode: this.filterMode(),
        service: this.selectedService() || undefined,
        minDurationMs: this.filterMode() === 'slow' ? this.minDurationMs() : undefined,
      }),
      services: this.api.getServices(start, end),
      dependencies: this.api.getDependencies(start, end),
    }).subscribe({
      next: ({ traces, services, dependencies }) => {
        this.traces.set(traces);
        this.services.set(services);
        this.dependencies.set(dependencies);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected applyFilter(): void {
    this.load();
  }

  protected loadAnalytics(): void {
    const svc = this.analyticsService();
    if (!svc) return;
    const { start, end } = this.timeRange.range();
    forkJoin({
      counts: this.api.getOperationCounts(svc, start, end),
      latencies: this.api.getLatencies(svc, start, end),
    }).subscribe(({ counts, latencies }) => {
      this.operationCounts.set(counts);
      this.latencies.set(latencies);
    });
  }

  protected navigate(traceId: string): void {
    this.router.navigate(['/traces', traceId]);
  }
}
