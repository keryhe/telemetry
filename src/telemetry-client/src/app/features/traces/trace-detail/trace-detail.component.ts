import { Component, Input, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, KeyValuePipe, SlicePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';

import { TracesApiService } from '../../../core/services/api/traces-api.service';
import { SpanModel, SpanStatusCode, SpanKind } from '../../../core/models/trace.models';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { StatCardComponent } from '../../../shared/components/stat-card/stat-card.component';
import { formatDuration } from '../../../shared/utils/chart.utils';

interface SpanNode extends SpanModel {
  children: SpanNode[];
  depth: number;
  durationMs: number;
  startOffsetMs: number;
  leftPct: number;
  widthPct: number;
  collapsed: boolean;
  serviceName: string;
}

const SERVICE_COLORS = [
  '#1976d2', '#388e3c', '#f57c00', '#7b1fa2',
  '#c62828', '#00838f', '#558b2f', '#4527a0',
];

@Component({
  selector: 'app-trace-detail',
  standalone: true,
  imports: [
    DatePipe, KeyValuePipe, SlicePipe, RouterLink,
    MatCardModule, MatButtonModule, MatIconModule, MatTooltipModule,
    MatChipsModule, MatProgressBarModule, EmptyStateComponent, StatCardComponent,
  ],
  templateUrl: './trace-detail.component.html',
  styleUrl: './trace-detail.component.scss',
})
export class TraceDetailComponent implements OnInit {
  @Input() id!: string;

  private readonly api = inject(TracesApiService);
  private readonly title = inject(Title);

  protected copied = signal(false);

  protected loading = signal(true);
  protected spans = signal<SpanModel[]>([]);
  protected selectedSpan = signal<SpanNode | null>(null);
  protected tree = signal<SpanNode[]>([]);

  protected traceStart = computed(() => {
    const s = this.spans();
    return s.length ? Math.min(...s.map((sp) => sp.startTimeUnixNano)) : 0;
  });
  protected traceEnd = computed(() => {
    const s = this.spans();
    return s.length ? Math.max(...s.map((sp) => sp.endTimeUnixNano)) : 0;
  });
  protected totalDurationMs = computed(() =>
    (this.traceEnd() - this.traceStart()) / 1_000_000
  );
  protected serviceNames = computed(() => [
    ...new Set(this.spans().map((s) => s.resource?.attributes?.['service.name'] as string ?? 'unknown')),
  ]);
  protected serviceColorMap = computed(() => {
    const map = new Map<string, string>();
    this.serviceNames().forEach((s, i) => map.set(s, SERVICE_COLORS[i % SERVICE_COLORS.length]));
    return map;
  });

  protected flatTree = computed<SpanNode[]>(() => this.flattenVisible(this.tree()));
  protected hasErrors = computed(() => this.spans().some((s) => s.statusCode === SpanStatusCode.Error));

  readonly formatDuration = formatDuration;
  readonly SpanStatusCode = SpanStatusCode;

  protected spanKindLabel(kind: SpanKind): string {
    return SpanKind[kind] ?? 'Unspecified';
  }

  protected statusLabel(code: SpanStatusCode): string {
    return SpanStatusCode[code] ?? 'Unset';
  }

  /** Convert OTLP unix-nanos to a JS Date (for the DatePipe). */
  protected nanoToDate(ns: number): Date {
    return new Date(ns / 1_000_000);
  }

  protected sortedEvents(node: SpanNode) {
    return [...node.events].sort((a, b) => a.timeUnixNano - b.timeUnixNano);
  }

  ngOnInit(): void {
    this.title.setTitle(`Trace: ${this.id.slice(0, 16)}`);
    this.api.getSpans(this.id).subscribe({
      next: (spans) => {
        this.spans.set(spans);
        this.tree.set(this.buildTree(spans));
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private buildTree(spans: SpanModel[]): SpanNode[] {
    const traceStartNs = this.traceStart();
    const durationNs = this.traceEnd() - traceStartNs || 1;

    const nodeMap = new Map<string, SpanNode>();
    for (const s of spans) {
      const durationMs = (s.endTimeUnixNano - s.startTimeUnixNano) / 1_000_000;
      const startOffsetMs = (s.startTimeUnixNano - traceStartNs) / 1_000_000;
      nodeMap.set(s.spanIdHex, {
        ...s,
        children: [],
        depth: 0,
        durationMs,
        startOffsetMs,
        leftPct: ((s.startTimeUnixNano - traceStartNs) / durationNs) * 100,
        widthPct: Math.max(0.2, ((s.endTimeUnixNano - s.startTimeUnixNano) / durationNs) * 100),
        collapsed: false,
        serviceName: s.resource?.attributes?.['service.name'] as string ?? 'unknown',
      });
    }

    const roots: SpanNode[] = [];
    for (const node of nodeMap.values()) {
      if (node.parentSpanIdHex && nodeMap.has(node.parentSpanIdHex)) {
        nodeMap.get(node.parentSpanIdHex)!.children.push(node);
      } else {
        roots.push(node);
      }
    }

    const setDepth = (n: SpanNode, d: number) => {
      n.depth = d;
      n.children.forEach((c) => setDepth(c, d + 1));
    };
    roots.forEach((r) => setDepth(r, 0));

    return roots;
  }

  private flattenVisible(nodes: SpanNode[]): SpanNode[] {
    const result: SpanNode[] = [];
    for (const n of nodes) {
      result.push(n);
      if (!n.collapsed) result.push(...this.flattenVisible(n.children));
    }
    return result;
  }

  protected toggleCollapse(node: SpanNode): void {
    node.collapsed = !node.collapsed;
    this.tree.update((t) => [...t]);
  }

  protected selectSpan(node: SpanNode): void {
    this.selectedSpan.set(node);
  }

  protected copyTraceId(): void {
    navigator.clipboard?.writeText(this.id).then(() => {
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 1500);
    });
  }

  protected colorFor(serviceName: string): string {
    return this.serviceColorMap().get(serviceName) ?? '#9e9e9e';
  }

  protected isError(node: SpanNode): boolean {
    return node.statusCode === SpanStatusCode.Error;
  }
}
