import { Component, Input, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe, KeyValuePipe, SlicePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';

import { TracesApiService } from '../../../core/services/api/traces-api.service';
import { SpanSearchHelpDialogComponent } from '../span-search-help-dialog/span-search-help-dialog.component';
import { SpanModel, SpanStatusCode, SpanKind } from '../../../core/models/trace.models';
import { EmptyStateComponent } from '../../../shared/components/empty-state/empty-state.component';
import { StatCardComponent } from '../../../shared/components/stat-card/stat-card.component';
import { formatDuration } from '../../../shared/utils/chart.utils';
import { parseSearchQuery, ParsedSearchQuery, SearchTerm } from '../../../shared/utils/search-query.parser';

interface SpanNode extends SpanModel {
  children: SpanNode[];
  depth: number;
  durationMs: number;
  startOffsetMs: number;
  leftPct: number;
  widthPct: number;
  collapsed: boolean;
  serviceName: string;
  /** Saturated self-time sub-bars (span interval minus children), trace-relative %. */
  selfSegments: { leftPct: number; widthPct: number }[];
  /** True when this span is on the trace's critical path. */
  onCriticalPath: boolean;
  /** Span-event ticks positioned on the trace-relative scale. */
  eventMarkers: { pct: number; isError: boolean; label: string }[];
}

const SERVICE_COLORS = [
  '#1976d2', '#f57c00', '#388e3c', '#7b1fa2',
  '#00838f', '#5d4037', '#558b2f', '#4527a0',
];

@Component({
  selector: 'app-trace-detail',
  standalone: true,
  imports: [
    DatePipe, KeyValuePipe, SlicePipe, RouterLink, FormsModule,
    MatCardModule, MatButtonModule, MatIconModule, MatTooltipModule,
    MatChipsModule, MatProgressBarModule, MatSlideToggleModule,
    MatFormFieldModule, MatInputModule, MatDialogModule,
    EmptyStateComponent, StatCardComponent,
  ],
  templateUrl: './trace-detail.component.html',
  styleUrl: './trace-detail.component.scss',
})
export class TraceDetailComponent implements OnInit {
  @Input() id!: string;

  private readonly api = inject(TracesApiService);
  private readonly title = inject(Title);
  private readonly dialog = inject(MatDialog);

  protected copied = signal(false);

  protected loading = signal(true);
  protected spans = signal<SpanModel[]>([]);
  protected selectedSpan = signal<SpanNode | null>(null);
  protected tree = signal<SpanNode[]>([]);
  protected showCriticalPath = signal(false);

  // Find-in-trace: match spans by name/service/attribute/min-duration, highlight, and cycle matches.
  protected findText = signal('');
  protected currentMatchIndex = signal(0);

  private readonly parsedFind = computed<ParsedSearchQuery>(() => parseSearchQuery(this.findText()));
  /** Matching spans, in visible (flattened) order, for highlight + prev/next navigation. */
  protected matches = computed<SpanNode[]>(() => {
    if (!this.findText().trim()) return [];
    const parsed = this.parsedFind();
    return this.flatTree().filter((n) => this.spanMatches(n, parsed));
  });
  protected matchIds = computed(() => new Set(this.matches().map((m) => m.spanIdHex)));
  protected currentMatch = computed<SpanNode | null>(() => this.matches()[this.currentMatchIndex()] ?? null);
  protected currentMatchId = computed(() => this.currentMatch()?.spanIdHex ?? null);

  constructor() {
    // A changed query restarts navigation at the first match.
    effect(() => {
      this.findText();
      untracked(() => this.currentMatchIndex.set(0));
    });
  }

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

  /** Start time (unix-nanos) of the earliest root span. */
  protected rootStartNano = computed(() => {
    const roots = this.tree();
    return roots.length ? Math.min(...roots.map((r) => r.startTimeUnixNano)) : this.traceStart();
  });

  /** Evenly spaced "nice" time-axis ticks across the trace duration. */
  protected ticks = computed<{ label: string; pct: number }[]>(() => {
    const total = this.totalDurationMs();
    if (!total || !isFinite(total)) return [];
    const step = this.niceStep(total / 5);
    const out: { label: string; pct: number }[] = [];
    for (let v = 0; v <= total + step * 1e-6; v += step) {
      out.push({ label: formatDuration(v), pct: (v / total) * 100 });
    }
    return out;
  });

  /** Spacing between vertical gridlines, as a percentage of the track width. */
  protected gridStepPct = computed(() => {
    const total = this.totalDurationMs();
    if (!total || !isFinite(total)) return 100;
    return (this.niceStep(total / 5) / total) * 100;
  });

  /** Round a raw interval up to a 1/2/5 * 10^n "nice" value. */
  private niceStep(raw: number): number {
    if (raw <= 0) return 1;
    const mag = Math.pow(10, Math.floor(Math.log10(raw)));
    const norm = raw / mag;
    const niceNorm = norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10;
    return niceNorm * mag;
  }

  /** Multi-line summary shown when hovering a Gantt bar. */
  protected barTooltip(node: SpanNode): string {
    return [
      node.name,
      `Service: ${node.serviceName}`,
      `Start: +${formatDuration(node.startOffsetMs)}`,
      `Duration: ${formatDuration(node.durationMs)}`,
      `Status: ${this.statusLabel(node.statusCode)}`,
    ].join('\n');
  }

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
        selfSegments: [],
        onCriticalPath: false,
        eventMarkers: [],
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

    this.computeOverlays(roots, traceStartNs, durationNs);
    this.markCriticalPath(roots);

    return roots;
  }

  /**
   * Per-node post-pass computing self-time segments and event markers on the
   * shared trace-relative percentage scale (matching leftPct/widthPct).
   */
  private computeOverlays(roots: SpanNode[], traceStartNs: number, durationNs: number): void {
    const seg = (startNs: number, endNs: number) => {
      const leftPct = this.clampPct(((startNs - traceStartNs) / durationNs) * 100);
      const rightPct = this.clampPct(((endNs - traceStartNs) / durationNs) * 100);
      return { leftPct, widthPct: Math.max(0, rightPct - leftPct) };
    };

    const walk = (n: SpanNode) => {
      const start = n.startTimeUnixNano;
      const end = Math.max(n.endTimeUnixNano, start);

      // Merge child intervals (clipped to the parent) into a coverage union.
      const merged: [number, number][] = [];
      const childIntervals = n.children
        .map((c) => [Math.max(c.startTimeUnixNano, start), Math.min(c.endTimeUnixNano, end)] as [number, number])
        .filter(([s, e]) => e > s)
        .sort((a, b) => a[0] - b[0]);
      for (const iv of childIntervals) {
        const last = merged[merged.length - 1];
        if (last && iv[0] <= last[1]) last[1] = Math.max(last[1], iv[1]);
        else merged.push([iv[0], iv[1]]);
      }

      // Self-time = the gaps in the span interval not covered by any child.
      const segs: { leftPct: number; widthPct: number }[] = [];
      let cursor = start;
      for (const [s, e] of merged) {
        if (s > cursor) segs.push(seg(cursor, s));
        cursor = Math.max(cursor, e);
      }
      if (cursor < end) segs.push(seg(cursor, end));
      // A leaf's self-time is its whole bar; a parent fully covered by children
      // legitimately has no self-time (stays faded).
      if (segs.length === 0 && n.children.length === 0) segs.push(seg(start, end));
      n.selfSegments = segs;

      n.eventMarkers = (n.events ?? []).map((ev) => ({
        pct: this.clampPct(((ev.timeUnixNano - traceStartNs) / durationNs) * 100),
        isError: /exception/i.test(ev.name) || n.statusCode === SpanStatusCode.Error,
        label: `${ev.name} · +${formatDuration((ev.timeUnixNano - traceStartNs) / 1_000_000)}`,
      }));

      n.children.forEach(walk);
    };
    roots.forEach(walk);
  }

  /**
   * Marks the critical path from the last-ending root, greedily following the
   * latest-ending child that finished before the running cursor.
   */
  private markCriticalPath(roots: SpanNode[]): void {
    if (!roots.length) return;
    const lastRoot = roots.reduce((a, b) => (b.endTimeUnixNano > a.endTimeUnixNano ? b : a));

    const mark = (node: SpanNode) => {
      node.onCriticalPath = true;
      let cursor = node.endTimeUnixNano;
      const kids = [...node.children].sort((a, b) => b.endTimeUnixNano - a.endTimeUnixNano);
      for (const child of kids) {
        if (child.startTimeUnixNano < cursor && child.endTimeUnixNano <= cursor) {
          mark(child);
          cursor = child.startTimeUnixNano;
        }
      }
    };
    mark(lastRoot);
  }

  private clampPct(v: number): number {
    return Math.min(100, Math.max(0, v));
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
    this.selectedSpan.update((current) => (current === node ? null : node));
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

  // ===========================================================================
  // FIND-IN-TRACE
  // ===========================================================================

  private spanMatches(node: SpanNode, parsed: ParsedSearchQuery): boolean {
    // A 32-hex "trace id" query is treated here as a span/trace id substring match.
    if (parsed.isTraceIdSearch) {
      const id = parsed.traceId!.toLowerCase();
      return node.spanIdHex.toLowerCase().includes(id) || node.traceIdHex.toLowerCase().includes(id);
    }
    if (!parsed.terms.length) return false;
    return parsed.terms.every((t) => this.termMatches(node, t));
  }

  private termMatches(node: SpanNode, term: SearchTerm): boolean {
    if (term.isAttributeFilter) {
      const key = (term.key ?? '').toLowerCase();
      const value = (term.value ?? '').toLowerCase();

      // Special key: min-duration threshold (accepts "500", "500ms", "1.5s").
      if (key === 'min-duration' || key === 'minduration' || key === 'duration') {
        const threshold = this.parseDurationMs(value);
        return threshold != null && node.durationMs >= threshold;
      }

      let hay: string | null;
      if (key === 'service' || key === 'service.name') hay = node.serviceName;
      else if (key === 'name' || key === 'operation' || key === 'op') hay = node.name;
      else {
        const v = this.lookupAttr(node, term.key ?? '');
        hay = v == null ? null : String(v);
      }
      if (hay == null) return false;
      const h = hay.toLowerCase();
      return term.isExactMatch ? h === value : h.includes(value);
    }

    const text = (term.freeText ?? '').toLowerCase();
    return node.name.toLowerCase().includes(text) || node.serviceName.toLowerCase().includes(text);
  }

  /** Look up an attribute value on the span, then its resource, by exact key. */
  private lookupAttr(node: SpanNode, key: string): unknown {
    if (node.attributes && Object.prototype.hasOwnProperty.call(node.attributes, key)) return node.attributes[key];
    const res = node.resource?.attributes;
    if (res && Object.prototype.hasOwnProperty.call(res, key)) return res[key];
    return null;
  }

  /** Parse a duration threshold in ms (bare number or ms), or seconds when suffixed with s. */
  private parseDurationMs(value: string): number | null {
    const m = value.trim().match(/^([\d.]+)\s*(ms|s)?$/);
    if (!m) return null;
    const n = parseFloat(m[1]);
    if (!isFinite(n)) return null;
    return m[2] === 's' ? n * 1000 : n;
  }

  protected nextMatch(): void {
    const n = this.matches().length;
    if (!n) return;
    this.currentMatchIndex.update((i) => (i + 1) % n);
    this.focusCurrentMatch();
  }

  protected prevMatch(): void {
    const n = this.matches().length;
    if (!n) return;
    this.currentMatchIndex.update((i) => (i - 1 + n) % n);
    this.focusCurrentMatch();
  }

  /** Select the current match and scroll its row into view. */
  private focusCurrentMatch(): void {
    const node = this.currentMatch();
    if (!node) return;
    this.selectedSpan.set(node);
    setTimeout(() =>
      document.querySelector(`[data-span-id="${node.spanIdHex}"]`)?.scrollIntoView({ block: 'center', behavior: 'smooth' })
    );
  }

  protected clearFind(): void {
    this.findText.set('');
    this.currentMatchIndex.set(0);
  }

  protected openSpanHelp(): void {
    this.dialog.open(SpanSearchHelpDialogComponent, { maxWidth: '720px', width: '90vw' });
  }
}
