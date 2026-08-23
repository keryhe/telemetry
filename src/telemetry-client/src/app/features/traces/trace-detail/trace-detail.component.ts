import { Component, Input, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe, DecimalPipe, KeyValuePipe, SlicePipe } from '@angular/common';
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
import { SERVICE_COLORS } from '../../../shared/utils/service-colors';
import { UrlStateService } from '../../../shared/utils/url-state';
import { loadPageState, savePageState } from '../../../shared/utils/page-state';

interface SpanNode extends SpanModel {
  children: SpanNode[];
  depth: number;
  durationMs: number;
  startOffsetMs: number;
  leftPct: number;
  widthPct: number;
  collapsed: boolean;
  serviceName: string;
  /** Self-time (span duration minus time covered by children), in ms. */
  selfMs: number;
  /** Saturated self-time sub-bars (span interval minus children), trace-relative %. */
  selfSegments: { leftPct: number; widthPct: number }[];
  /** True when this span is on the trace's critical path. */
  onCriticalPath: boolean;
  /** Span-event ticks positioned on the trace-relative scale. */
  eventMarkers: { pct: number; isError: boolean; label: string }[];
}

type DetailView = 'timeline' | 'statistics' | 'flame' | 'json';

/** Aggregated per-service / per-operation timing row for the Statistics view. */
interface StatRow {
  key: string;
  count: number;
  selfMs: number;
  totalMs: number;
  avgMs: number;
  maxMs: number;
  /** Share of total self-time across the trace (%). */
  pct: number;
}

/** Delimiter for aggregated call-path keys (unlikely to occur in an operation name). */
const PATH_SEP = '';

const STATE_KEY = 'state.trace-detail';

/**
 * Name-column indent budget. The per-level step shrinks so the *deepest* row's indent never
 * exceeds INDENT_BUDGET px — otherwise a deeply nested trace eats the whole (border-box)
 * column from the left and the operation name is clipped to nothing.
 */
const INDENT_BUDGET = 160;
const INDENT_STEP_MAX = 16;
const INDENT_STEP_MIN = 4;

/** Resizable name column: bounds, and the room always reserved for the Gantt track. */
const NAME_COL_DEFAULT = 280;
const NAME_COL_MIN = 180;
const NAME_COL_MAX = 720;
const GANTT_MIN = 240;

/** Chrome around the operation name inside `.span-name` (collapse icon + dot + gaps + padding). */
const NAME_FIXED_PX = 52;

/** A rendered flame cell — shared by the time-ordered and aggregated layouts. */
interface FlameCell {
  /** Representative span (click → reveal in the timeline). */
  node: SpanNode;
  label: string;
  /** Left edge, as a percentage of the flame width. */
  x: number;
  /** Width, as a percentage of the flame width. */
  width: number;
  /** Vertical offset in px (depth × row height). */
  y: number;
  color: string;
  isError: boolean;
  tooltip: string;
  /** Self-time as a fraction of the cell's own duration (drives the time-mode underline). */
  selfFraction: number;
  /** Merged-span count — 1 in time mode. */
  count: number;
  /** Call-path key, used as the zoom target in aggregated mode. */
  path: string;
}

/** A merged call-path frame in the aggregated flame layout. */
interface AggFrame {
  name: string;
  path: string;
  /** Summed own self-time across merged spans (ms). */
  selfMs: number;
  /** selfMs + summed children totals — the frame's inclusive self-time (ms). */
  totalMs: number;
  count: number;
  node: SpanNode;
  serviceName: string;
  children: AggFrame[];
}

/** Mutable accumulator used while merging spans into the aggregated tree. */
interface AggBuild {
  name: string;
  path: string;
  selfMs: number;
  count: number;
  node: SpanNode;
  serviceName: string;
  children: Map<string, AggBuild>;
}

@Component({
  selector: 'app-trace-detail',
  standalone: true,
  imports: [
    DatePipe, DecimalPipe, KeyValuePipe, SlicePipe, RouterLink, FormsModule,
    MatCardModule, MatButtonModule, MatIconModule, MatTooltipModule,
    MatChipsModule, MatProgressBarModule, MatSlideToggleModule,
    MatFormFieldModule, MatInputModule, MatDialogModule,
    EmptyStateComponent, StatCardComponent,
  ],
  templateUrl: './trace-detail.component.html',
  styleUrl: './trace-detail.component.scss',
  // Published on the host so the sticky header spacer and every row read one source of truth.
  host: { '[style.--name-col-w.px]': 'nameColWidth()' },
})
export class TraceDetailComponent implements OnInit {
  @Input() id!: string;

  private readonly api = inject(TracesApiService);
  private readonly title = inject(Title);
  private readonly dialog = inject(MatDialog);
  private readonly urlState = inject(UrlStateService);

  protected copied = signal(false);
  protected linkCopied = signal(false);

  protected loading = signal(true);
  protected spans = signal<SpanModel[]>([]);
  protected selectedSpan = signal<SpanNode | null>(null);
  protected tree = signal<SpanNode[]>([]);
  protected showCriticalPath = signal(false);

  private readonly saved = loadPageState(STATE_KEY, { nameColWidth: NAME_COL_DEFAULT });

  /** Width of the timeline's name column, in px — drag-resizable, persisted across visits. */
  protected nameColWidth = signal(
    Math.max(NAME_COL_MIN, Math.min(NAME_COL_MAX, this.saved.nameColWidth)));
  /** True while the divider is being dragged (suppresses hover chrome + text selection). */
  protected resizing = signal(false);

  /** Active detail view: waterfall timeline, aggregate statistics, flame graph, or raw JSON. */
  protected view = signal<DetailView>('timeline');
  protected readonly views: { id: DetailView; label: string; icon: string }[] = [
    { id: 'timeline', label: 'Timeline', icon: 'view_timeline' },
    { id: 'statistics', label: 'Statistics', icon: 'table_chart' },
    { id: 'flame', label: 'Flame', icon: 'local_fire_department' },
    { id: 'json', label: 'JSON', icon: 'data_object' },
  ];

  /** Jump-to-error cursor over the trace's error spans. */
  protected currentErrorIndex = signal(0);

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

    effect(() => savePageState(STATE_KEY, { nameColWidth: this.nameColWidth() }));
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

  /** Every span node in tree order, ignoring collapse (for stats/flame/error nav). */
  protected allNodes = computed<SpanNode[]>(() => this.flattenAll(this.tree()));

  // --- Name column: adaptive indent, and which rows are actually clipped. ---

  /** Deepest span in the trace, ignoring collapse — so the step doesn't jitter on expand/collapse. */
  protected maxDepth = computed(() =>
    this.allNodes().reduce((m, n) => Math.max(m, n.depth), 0));

  /** Per-level indent, shrunk so `maxDepth × step` fits within INDENT_BUDGET. */
  protected indentStep = computed(() => {
    const d = this.maxDepth();
    if (d <= 0) return INDENT_STEP_MAX;
    return Math.max(INDENT_STEP_MIN, Math.min(INDENT_STEP_MAX, INDENT_BUDGET / d));
  });

  /**
   * Visible rows whose name can't fit at the current width/indent. Drives the hover pop-out, so
   * names that already fit don't flash a pointless chip. Recomputes only when the flattened tree,
   * the indent step, or the column width changes.
   */
  protected clippedIds = computed(() => {
    const step = this.indentStep();
    const avail = this.nameColWidth() - NAME_FIXED_PX;
    return new Set(
      this.flatTree()
        .filter((n) => this.measureName(n.name) > avail - n.depth * step)
        .map((n) => n.spanIdHex));
  });

  /** child span id → parent node, for expanding ancestors when revealing a span. */
  private parentMap = computed<Map<string, SpanNode>>(() => {
    const map = new Map<string, SpanNode>();
    const walk = (n: SpanNode) => n.children.forEach((c) => { map.set(c.spanIdHex, n); walk(c); });
    this.tree().forEach(walk);
    return map;
  });

  // --- Statistics view: aggregate self-time by service and by operation. ---
  protected serviceStats = computed<StatRow[]>(() => this.aggregate((n) => n.serviceName));
  protected operationStats = computed<StatRow[]>(() => this.aggregate((n) => n.name));

  private aggregate(keyFn: (n: SpanNode) => string): StatRow[] {
    const nodes = this.allNodes();
    const totalSelf = nodes.reduce((a, n) => a + n.selfMs, 0) || 1;
    const groups = new Map<string, { count: number; selfMs: number; totalMs: number; maxMs: number }>();
    for (const n of nodes) {
      const key = keyFn(n);
      const g = groups.get(key) ?? { count: 0, selfMs: 0, totalMs: 0, maxMs: 0 };
      g.count++;
      g.selfMs += n.selfMs;
      g.totalMs += n.durationMs;
      g.maxMs = Math.max(g.maxMs, n.durationMs);
      groups.set(key, g);
    }
    return [...groups.entries()]
      .map(([key, g]) => ({
        key, count: g.count, selfMs: g.selfMs, totalMs: g.totalMs, maxMs: g.maxMs,
        avgMs: g.totalMs / g.count, pct: (g.selfMs / totalSelf) * 100,
      }))
      .sort((a, b) => b.selfMs - a.selfMs);
  }

  // --- Flame graph ---
  protected readonly flameRowHeight = 32;
  /** Time-ordered (1:1 with the timeline) vs. aggregated-by-call-path self-time. */
  protected flameMode = signal<'time' | 'aggregated'>('aggregated');
  /** Aggregated-mode drill-down root (a call-path); null = whole trace. */
  protected zoomPath = signal<string | null>(null);
  /** Operation hovered in the flame graph, to highlight all its instances. */
  protected hoveredOp = signal<string | null>(null);

  /** Total self-time across the trace, for %-of-trace tooltips. */
  private traceSelfMs = computed(() => this.allNodes().reduce((a, n) => a + n.selfMs, 0) || 1);

  /** Cells for the active flame layout. */
  protected flameCells = computed<FlameCell[]>(() =>
    this.flameMode() === 'aggregated' ? this.buildAggCells() : this.buildTimeCells()
  );

  /** Flame height (px), sized to the deepest visible cell. */
  protected flameHeight = computed(() => {
    const maxY = this.flameCells().reduce((m, c) => Math.max(m, c.y), 0);
    return maxY + this.flameRowHeight;
  });

  /** Breadcrumb segments for the aggregated-mode zoom path. */
  protected zoomCrumbs = computed<string[]>(() => {
    const zp = this.zoomPath();
    return zp ? zp.split(PATH_SEP) : [];
  });

  /** Time-ordered cells: one per span, positioned on the trace time axis. */
  private buildTimeCells(): FlameCell[] {
    const traceSelf = this.traceSelfMs();
    return this.allNodes().map((n) => ({
      node: n,
      label: n.name,
      x: n.leftPct,
      width: n.widthPct,
      y: n.depth * this.flameRowHeight,
      color: this.colorFor(n.serviceName),
      isError: this.isError(n),
      selfFraction: n.durationMs > 0 ? Math.min(1, n.selfMs / n.durationMs) : 1,
      count: 1,
      path: '',
      tooltip: [
        n.name,
        `Service: ${n.serviceName}`,
        `Duration: ${formatDuration(n.durationMs)}`,
        `Self: ${formatDuration(n.selfMs)} (${((n.selfMs / traceSelf) * 100).toFixed(1)}% of trace)`,
      ].join('\n'),
    }));
  }

  /** Merge the span tree into an aggregated call-path tree (top frames, sorted by total). */
  private buildAggFrames(): AggFrame[] {
    const rootMap = new Map<string, AggBuild>();
    for (const r of this.tree()) this.mergeInto(rootMap, r, '');
    return [...rootMap.values()].map((b) => this.finalizeFrame(b)).sort((a, b) => b.totalMs - a.totalMs);
  }

  private mergeInto(map: Map<string, AggBuild>, node: SpanNode, parentPath: string): void {
    const path = parentPath ? parentPath + PATH_SEP + node.name : node.name;
    let f = map.get(node.name);
    if (!f) {
      f = { name: node.name, path, selfMs: 0, count: 0, node, serviceName: node.serviceName, children: new Map() };
      map.set(node.name, f);
    }
    f.selfMs += node.selfMs;
    f.count++;
    for (const c of node.children) this.mergeInto(f.children, c, path);
  }

  private finalizeFrame(b: AggBuild): AggFrame {
    const children = [...b.children.values()].map((c) => this.finalizeFrame(c)).sort((x, y) => y.totalMs - x.totalMs);
    const totalMs = b.selfMs + children.reduce((a, c) => a + c.totalMs, 0);
    return {
      name: b.name, path: b.path, selfMs: b.selfMs, totalMs, count: b.count,
      node: b.node, serviceName: b.serviceName, children,
    };
  }

  private findFrame(frames: AggFrame[], path: string): AggFrame | null {
    for (const f of frames) {
      if (f.path === path) return f;
      const found = this.findFrame(f.children, path);
      if (found) return found;
    }
    return null;
  }

  /** Aggregated icicle cells: width ∝ inclusive self-time, children left-aligned under parents. */
  private buildAggCells(): FlameCell[] {
    const all = this.buildAggFrames();
    const zp = this.zoomPath();
    let display = all;
    if (zp) {
      const target = this.findFrame(all, zp);
      if (target) display = [target];
    }
    const total = display.reduce((a, f) => a + f.totalMs, 0) || 1;
    const scale = 100 / total;
    const traceSelf = this.traceSelfMs();
    const cells: FlameCell[] = [];

    const walk = (f: AggFrame, left: number, depth: number, parentTotal: number): void => {
      const pctTrace = ((f.totalMs / traceSelf) * 100).toFixed(1);
      const pctParent = parentTotal > 0 ? ((f.totalMs / parentTotal) * 100).toFixed(1) : null;
      cells.push({
        node: f.node,
        label: f.count > 1 ? `${f.name} ×${f.count}` : f.name,
        x: left,
        width: f.totalMs * scale,
        y: depth * this.flameRowHeight,
        color: this.colorFor(f.serviceName),
        isError: this.isError(f.node),
        selfFraction: 0,
        count: f.count,
        path: f.path,
        tooltip: [
          f.name,
          `Service: ${f.serviceName}`,
          `Calls: ${f.count}`,
          `Own self: ${formatDuration(f.selfMs)}`,
          `Subtree: ${formatDuration(f.totalMs)} (${pctTrace}% of trace)`,
          pctParent ? `${pctParent}% of parent` : '',
        ].filter(Boolean).join('\n'),
      });
      let cursor = left;
      for (const c of f.children) { walk(c, cursor, depth + 1, f.totalMs); cursor += c.totalMs * scale; }
    };

    let cursor = 0;
    for (const f of display) { walk(f, cursor, 0, total); cursor += f.totalMs * scale; }
    return cells;
  }

  protected setFlameMode(mode: 'time' | 'aggregated'): void {
    this.flameMode.set(mode);
    this.zoomPath.set(null);
  }

  /** Zoom to the i-th breadcrumb segment (0-based). */
  protected zoomToCrumb(i: number): void {
    this.zoomPath.set(this.zoomCrumbs().slice(0, i + 1).join(PATH_SEP));
  }

  protected resetZoom(): void { this.zoomPath.set(null); }

  // --- Raw JSON view. ---
  protected spansJson = computed(() => JSON.stringify(this.spans(), null, 2));

  // --- Jump-to-error: all error spans in tree order. ---
  protected errorSpans = computed<SpanNode[]>(() => this.allNodes().filter((n) => this.isError(n)));

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
  // --- Name-column resizing ---

  /**
   * Drag the divider between the name column and the Gantt track. Uses pointer capture, so all
   * moves retarget to the handle — no document-level listeners, and the drag survives the pointer
   * leaving the element.
   */
  protected onResizeStart(ev: PointerEvent): void {
    ev.preventDefault();
    const el = ev.currentTarget as HTMLElement;
    const startX = ev.clientX;
    const startW = this.nameColWidth();
    const avail = el.parentElement?.clientWidth ?? NAME_COL_MAX + GANTT_MIN;
    const max = Math.max(NAME_COL_MIN, Math.min(NAME_COL_MAX, avail - GANTT_MIN));

    el.setPointerCapture(ev.pointerId);
    this.resizing.set(true);

    const move = (e: PointerEvent) =>
      this.nameColWidth.set(Math.max(NAME_COL_MIN, Math.min(max, startW + e.clientX - startX)));
    const stop = () => {
      el.releasePointerCapture(ev.pointerId);
      el.removeEventListener('pointermove', move);
      el.removeEventListener('pointerup', stop);
      el.removeEventListener('pointercancel', stop);
      this.resizing.set(false);
    };

    el.addEventListener('pointermove', move);
    el.addEventListener('pointerup', stop);
    el.addEventListener('pointercancel', stop);
  }

  /** Double-click the divider: widen the column to fit the widest currently-visible name. */
  protected autoFitNameCol(): void {
    const step = this.indentStep();
    const widest = this.flatTree().reduce(
      (m, n) => Math.max(m, n.depth * step + NAME_FIXED_PX + this.measureName(n.name)), 0);
    this.nameColWidth.set(
      Math.max(NAME_COL_MIN, Math.min(NAME_COL_MAX, Math.ceil(widest) + 8)));
  }

  /** Lazily-built canvas context for measuring `.op-name` text without touching the DOM. */
  private measureCtx: CanvasRenderingContext2D | null = null;

  private measureName(text: string): number {
    if (!this.measureCtx) {
      const ctx = document.createElement('canvas').getContext('2d');
      if (!ctx) return text.length * 6.6; // coarse fallback (SSR / canvas unavailable)
      ctx.font = `12px ${getComputedStyle(document.body).fontFamily}`;
      this.measureCtx = ctx;
    }
    return this.measureCtx.measureText(text).width;
  }

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
        this.applyDeepLink();
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
        selfMs: 0,
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
      let coveredNs = 0;
      for (const [s, e] of merged) {
        if (s > cursor) segs.push(seg(cursor, s));
        cursor = Math.max(cursor, e);
        coveredNs += e - s;
      }
      if (cursor < end) segs.push(seg(cursor, end));
      // A leaf's self-time is its whole bar; a parent fully covered by children
      // legitimately has no self-time (stays faded).
      if (segs.length === 0 && n.children.length === 0) segs.push(seg(start, end));
      n.selfSegments = segs;
      n.selfMs = Math.max(0, (end - start - coveredNs) / 1_000_000);

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

  /** Flatten every node in tree order, regardless of collapse state. */
  private flattenAll(nodes: SpanNode[]): SpanNode[] {
    const result: SpanNode[] = [];
    for (const n of nodes) {
      result.push(n);
      result.push(...this.flattenAll(n.children));
    }
    return result;
  }

  /** Expand every collapsed ancestor so `node`'s row is present in the timeline. */
  private expandAncestors(node: SpanNode): void {
    const parents = this.parentMap();
    let changed = false;
    let cur = parents.get(node.spanIdHex);
    while (cur) {
      if (cur.collapsed) { cur.collapsed = false; changed = true; }
      cur = parents.get(cur.spanIdHex);
    }
    if (changed) this.tree.update((t) => [...t]);
  }

  /** Scroll a span's row into view (after any pending render). */
  private scrollToSpan(spanId: string): void {
    setTimeout(() =>
      document.querySelector(`[data-span-id="${spanId}"]`)?.scrollIntoView({ block: 'center', behavior: 'smooth' })
    );
  }

  /** Reveal + select a span in the timeline (used by deep-link, error jump, find). */
  private revealSpan(node: SpanNode): void {
    if (this.view() !== 'timeline') this.view.set('timeline');
    this.expandAncestors(node);
    this.selectedSpan.set(node);
    this.writeSpanToUrl(node.spanIdHex);
    this.scrollToSpan(node.spanIdHex);
  }

  protected toggleCollapse(node: SpanNode): void {
    node.collapsed = !node.collapsed;
    this.tree.update((t) => [...t]);
  }

  protected selectSpan(node: SpanNode): void {
    const next = this.selectedSpan() === node ? null : node;
    this.selectedSpan.set(next);
    this.writeSpanToUrl(next?.spanIdHex ?? null);
  }

  /** Mirror the selected span id into the URL (`span` query param) for deep-linking. */
  private writeSpanToUrl(spanId: string | null): void {
    this.urlState.patch({ span: spanId });
  }

  /** On load, honor a `?span=` deep link: reveal + select that span. */
  private applyDeepLink(): void {
    const spanId = this.urlState.get('span');
    if (!spanId) return;
    const node = this.allNodes().find((n) => n.spanIdHex === spanId);
    if (node) this.revealSpan(node);
  }

  protected copySpanLink(): void {
    navigator.clipboard?.writeText(window.location.href).then(() => {
      this.linkCopied.set(true);
      setTimeout(() => this.linkCopied.set(false), 1500);
    });
  }

  // ===========================================================================
  // JUMP-TO-ERROR
  // ===========================================================================

  /** Reveal the next error span, cycling through all error spans in order. */
  protected nextError(): void {
    const errors = this.errorSpans();
    if (!errors.length) return;
    const i = (this.currentErrorIndex() + 1) % errors.length;
    this.currentErrorIndex.set(i);
    this.revealSpan(errors[i]);
  }

  protected prevError(): void {
    const errors = this.errorSpans();
    if (!errors.length) return;
    const i = (this.currentErrorIndex() - 1 + errors.length) % errors.length;
    this.currentErrorIndex.set(i);
    this.revealSpan(errors[i]);
  }

  // ===========================================================================
  // RAW JSON
  // ===========================================================================

  protected copyJson(): void {
    navigator.clipboard?.writeText(this.spansJson());
  }

  protected downloadJson(): void {
    const blob = new Blob([this.spansJson()], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `trace-${this.id.slice(0, 16)}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }

  /**
   * Flame-cell click. Aggregated mode: plain click drills into the frame, ⌘/Ctrl-click reveals
   * the span in the timeline. Time mode: click reveals the span.
   */
  protected onFlameCellClick(cell: FlameCell, ev: MouseEvent): void {
    if (this.flameMode() === 'aggregated' && !(ev.ctrlKey || ev.metaKey)) {
      this.zoomPath.set(cell.path);
    } else {
      this.revealSpan(cell.node);
    }
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
    this.expandAncestors(node);
    this.selectedSpan.set(node);
    this.writeSpanToUrl(node.spanIdHex);
    this.scrollToSpan(node.spanIdHex);
  }

  protected clearFind(): void {
    this.findText.set('');
    this.currentMatchIndex.set(0);
  }

  protected openSpanHelp(): void {
    this.dialog.open(SpanSearchHelpDialogComponent, { maxWidth: '720px', width: '90vw' });
  }
}
