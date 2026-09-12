# Dashboard Refactor Plan

Status: **proposed** — no code written yet. All §5 decisions resolved.
Created: 2026-09-12 · Revised: 2026-09-12 (decisions folded in; Phase 5 restructured)
Scope: `src/telemetry-client/src/app/features/dashboard/` plus small, additive backend changes.

---

## 1. Goal

Turn the dashboard from a "here is some telemetry" page into a **triage page** that answers, top
to bottom:

1. *Is anything wrong?* — RED KPI strip (Rate, Errors, Duration)
2. *When did it start?* — volume + latency time series
3. *Where is it?* — per-service health table
4. *Show me one* — recent errors / slowest traces
5. *Anything else I care about?* — user-configured metric charts

Secondary goals: drop the page's dominant network cost, and make the layout work from a 375px
phone up to a 3440px ultrawide.

---

## 2. Findings that shape the plan

These were verified against the code and are the reason the phases are ordered the way they are.

### 2.1 The metrics asked for are already free

`TraceVolumeBucket` already carries `SumDurationMs`
([Query.cs:104](../src/Keryhe.Telemetry.Core/Models/Query.cs#L104)), and the Angular `TimeBucket`
mirrors it ([chart.utils.ts:35](../src/telemetry-client/src/app/shared/utils/chart.utils.ts#L35)).
Nothing consumes it today.

- **Avg duration** = `Σ sumDurationMs / Σ count` — free
- **Traces/sec** = `Σ count / windowSeconds` — free
- **Error rate** — already computed in the component

All three come out of the histogram response the dashboard *already* fetches. Phase 2 adds **zero**
new HTTP requests.

### 2.2 The `getLogs` call is unbounded and ~entirely wasted

`GET /api/logs` has no limit parameter at all
([LogsController.cs:19](../src/Keryhe.Telemetry.Api/Controllers/LogsController.cs#L19)) — it
returns every log record in the window. The dashboard uses the result for `logs().length` and
nothing else, while *also* fetching `logHist`, whose severity buckets sum to the same number.

This is the page's dominant cost on any realistic window. Phase 1 removes it.

### 2.3 Both histogram endpoints are full in-memory scans

`GetTraceHistogramAsync` ([TraceReadRepositoryBase.cs:337](../src/Keryhe.Telemetry.Core/Data/Read/TraceReadRepositoryBase.cs#L337))
delegates to `ComputeTraceInfosAsync`, which pulls every raw span in the window into memory and
groups in C#. This is the real budget constraint:

> **Widgets derived from an already-materialized payload are free. Widgets needing another
> full-window scan are expensive.**

The corollary drives Phase 5: `ComputeTraceInfosAsync` already produces exactly the `List<TraceInfo>`
that per-service stats needs — it just groups it by time instead of by service. **Grouping the same
list a second way costs nothing.** The whole plan adds no scans at any phase.

### 2.4 Percentiles are the one genuine data gap — and they are cheap to close

p95/p99 exist only in `GetOperationStatsAsync`
([TraceReadRepositoryBase.cs:581](../src/Keryhe.Telemetry.Core/Data/Read/TraceReadRepositoryBase.cs#L581)),
which requires a specific service name and does its own full scan — unusable for an auto-refreshing
dashboard.

But `GetTraceHistogramAsync` **already holds every trace's duration in memory** at
[line 361](../src/Keryhe.Telemetry.Core/Data/Read/TraceReadRepositoryBase.cs#L361), and a
`Percentile` helper already exists in the same class
([line 680](../src/Keryhe.Telemetry.Core/Data/Read/TraceReadRepositoryBase.cs#L680)). Adding
per-bucket percentiles is a sort per bucket:

- no new query, no new endpoint
- no SQL, so **no per-provider dialect work** (shared base class, all five providers inherit)
- no schema change
- no DTO change — `TracesController` returns `TraceVolumeBucket` directly
  ([TracesController.cs:85](../src/Keryhe.Telemetry.Api/Controllers/TracesController.cs#L85))

Highest value-per-effort item in the plan. That is why it is Phase 4 and not "someday".

### 2.5 Layout blast radius

| Class | Used by | Safe to change? |
|---|---|---|
| `.chart-grid` | dashboard **only** | yes — free to replace |
| `.stat-grid` | 6 pages | additive only |
| `.page-container` | 7 pages | **no** — use a dashboard-scoped modifier |

`.page-container { max-width: 1600px }`
([styles.scss:42](../src/telemetry-client/src/styles.scss#L42)) is the widescreen ceiling, and
`.chart-grid` is a hard `1fr 1fr` with one 900px breakpoint
([styles.scss:55](../src/telemetry-client/src/styles.scss#L55)).

### 2.6 Naming accuracy

`traceDuration` is **root-span wall time for the whole trace**, not HTTP response time — for a trace
that fans out to async work it includes that work. See Decision 1 (§5).

### 2.7 The Logs page is already permalink-capable

`logs.component.ts` reads `severity`, `page`, and `size` from query params via `readNum`
([logs.component.ts:106](../src/telemetry-client/src/app/features/logs/logs.component.ts#L106)),
and its severity options are `9=Info / 13=Warn / 17=Error / 21=Fatal`
([logs.component.ts:148](../src/telemetry-client/src/app/features/logs/logs.component.ts#L148)) with
`minSeverity` (>=) semantics.

So a link to `/logs?severity=17` lands on exactly Error+Fatal — **matching the Error Logs card's sum
precisely, with zero changes to the Logs page.** Used by Decision 2.

---

## 3. Phases

Each phase is independently shippable and leaves the page in a working state.

---

### Phase 1 — Delete the unbounded log fetch

**Why first:** pure win, no design dependency, biggest single perf improvement.

**Changes** — `dashboard.component.ts`:
- Remove `logsApi.getLogs` from the `forkJoin`; remove the `logs` signal.
- Store the log histogram in a new `logHistogram = signal<LogBucket[]>([])` (currently passed
  straight to `buildCharts` and discarded).
- Derive `logTotal` and `logErrorCount` (`error + fatal`) from `logHistogram()`.
- Drop the now-unused `LogRecord` / `getServiceName` imports.

`dashboard.component.html`: point the "Log Events" card at the derived count.

**⚠ Behavioural change, intentional:** the current raw fetch ignores the service filter and is
filtered client-side, while `logHist` passes `service` to the server. **The displayed number will
change when a service is selected — because it is currently wrong.** Call this out in the commit
message.

**Acceptance**
- No `GET /api/logs?...` request from the dashboard (check the network panel).
- Log Events matches the Logs page total for the same window and service.
- Log Severity chart unchanged.

---

### Phase 2 — RED KPI strip, sparklines, latency chart

**Why second:** delivers the headline request. Still zero new requests.

**`chart.utils.ts`** — add `buildSparklineOptions(series, isDark, color)`: ~40px, no axes, no grid,
no tooltip, `sparkline: { enabled: true }`.

**`stat-card.component.ts`** — add optional `@Input() sparkline?: [number, number][]` and
`@Input() sparklineColor?: string`, rendered under the value. Purely additive; the six existing
call sites elsewhere are untouched.

**`dashboard.component.ts`** — new computeds off `traceHistogram()`:

| Computed | Formula |
|---|---|
| `tracesPerSecond` | `totalTraces() / windowSeconds` |
| `avgDurationMs` | `Σ sumDurationMs / Σ count` |
| `countSeries` / `errorRateSeries` / `avgDurationSeries` | per-bucket, for sparklines |

Add a **Latency over time** chart (avg per bucket).

> **Empty-bucket rule:** a bucket with `count === 0` must plot `null`, **not** `0`. Plotting zero
> makes idle periods look like 0ms responses and drags the visual mean down. Applies to the latency
> chart and to the avg-duration sparkline.

**KPI strip (6 cards)**, per Decisions 1, 2 and 5:

| Card | Label | Color | Behaviour |
|---|---|---|---|
| Rate | `Traces/sec` | default | tooltip: root-span wall time basis |
| Duration | `Avg Trace Duration` | default | tooltip: includes async work |
| Duration | `p95 Trace Duration` | default | *placeholder until Phase 4* |
| Errors | `Error Rate` | warn ≥1%, error ≥5% | constants, not literals |
| Logs | `Error Logs` | **always default** | `routerLink="/logs?severity=17"` |
| Scale | `Services` | default | — |

Threshold constants at the top of the component, replacing the hardcoded `errorRate() > 0.05` in the
template:

```ts
const ERROR_RATE_WARN = 0.01;
const ERROR_RATE_ERROR = 0.05;
```

`StatCardComponent` already supports `'default' | 'error' | 'warn' | 'success'`, so the orange warn
tier is free — today 0%, 0.8% and 6% all render identically below the 5% line.

**Deferred:** a numeric "vs previous window" delta needs a second histogram query for the preceding
window. Sparkline shape alone carries most of the signal. Revisit only if asked.

**Acceptance**
- Six cards render with sparklines; `avgDurationMs` matches a hand calculation from the raw
  histogram JSON.
- Request count identical to Phase 1.
- An all-zero window renders gaps, not a flat 0ms line.
- Error Logs card navigates to `/logs?severity=17` and the destination count **matches the card**.
- Error Rate card is neutral / orange / red at 0.5% / 2% / 8%.
- Light and dark themes both legible.

---

### Phase 3 — Layout and responsive rework

**Why third:** by now the final widget inventory is known, so the grid is designed once.

**Target layout**

```
┌─ KPI strip ─────────────────────────────────────────────┐
│ Traces/s │ Avg dur │ p95 │ Error % │ Err logs │ Services │
├─ Trace Volume (wide) ──────────┬─ Latency over time ────┤
├─ Service Health (Phase 5) ─────────────────────────────┤
├─ Recent Errors ────────────────┬─ Slowest Traces ───────┤
├─ Custom Metrics (Phase 6) ─────────────────────────────┤
└─────────────────────────────────────────────────────────┘
```

**Grid** — replace `.chart-grid` (dashboard-only, so free to drop) with a 12-column
`.dashboard-grid`; each widget declares its span per tier.

| Viewport | Columns | KPI strip | Charts |
|---|---|---|---|
| < 768 | 1 | 1 across | full width |
| 768–1280 | 6 | 2–3 across | full width |
| 1280–1920 | 12 | 6 across | 2 side by side |
| 1920–2560 | 12 | 6 across | 2 at 7/5 split |
| > 2560 | 12 | 6 across | 3 side by side |

**Three specifics that matter more than the breakpoints:**

1. **Cap width at 2400px — do not remove the cap.** An unbounded 3440px time series is a 12:1 sliver
   where vertical variation disappears; that is *worse* than letterboxing. Spend extra width on
   **more columns**, not wider ones. Use a `.page-container--wide` modifier scoped to the dashboard —
   `.page-container` itself is shared by 7 pages (§2.5) — and express the value as a custom property
   `--dashboard-max-width: 2400px` per Decision 4.
2. **`aspect-ratio` instead of `height: 220`.** A fixed 220px is right at 800px wide and squashed at
   1700px. ApexCharts needs a concrete pixel height, so this means a small `ResizeObserver`-backed
   directive feeding height from width. Reusable across every chart in the app.
3. **Container queries for the widgets themselves**, so a widget lays out from *its own* width and
   works at 1/3 width on an ultrawide and full width on a laptop without knowing the viewport. This
   is also what lets the Phase 6 grid work without special-casing.

**Acceptance:** verified at 375 / 768 / 1280 / 1920 / 2560 / 3440px; no horizontal body scroll at
any width; charts re-render (not just stretch) on resize; overriding `--dashboard-max-width` in one
place changes the cap.

---

### Phase 4 — Per-bucket percentiles (backend)

**Why here:** closes the one real data gap. Small, self-contained, no schema/provider work (§2.4).

1. `Query.cs` — add `P50Ms`, `P95Ms`, `P99Ms` to `TraceVolumeBucket`.
2. `TraceReadRepositoryBase.GetTraceHistogramAsync` — accumulate a `List<double>` of durations per
   bucket alongside the existing `sumDurationMs[]`, sort each, and fill the three fields using the
   existing private `Percentile` helper (same class, line 680).
3. `chart.utils.ts` — add the three fields to the `TimeBucket` interface.
4. Dashboard — fill the p95 KPI card; add a **p95 line** to the latency chart next to avg.

**Not touched:** SQL, the five provider classes, `TracesController` (returns the model directly),
DB schema, `apply-schema.sh`.

**Cost note:** one `List<double>` per bucket, bounded by the trace count already materialized in
memory. At the 500-bucket clamp this is negligible relative to the existing raw-span load.

**Acceptance**
- `p50 ≤ p95 ≤ p99 ≤ max` for every bucket.
- A single-trace bucket returns that trace's duration for all three.
- An empty bucket returns 0 for all three and is rendered as a **gap**, per the Phase 2 rule.
- All five providers return populated percentiles (base class — spot-check two).

---

### Phase 5 — Service health table, via a dashboard overview endpoint

**Why here:** highest-value *new* widget. Per Decision 3 it now costs **no additional scan** — the
structure changed from "new endpoint that scans again" to "one endpoint that groups the existing
scan twice".

#### Why not simply extend `/histogram`

`getTraceHistogram` has two callers — the dashboard and
[trace-list.component.ts:445](../src/telemetry-client/src/app/features/traces/trace-list/trace-list.component.ts#L445).
Options considered:

| Approach | Verdict |
|---|---|
| Fatten `/histogram` unconditionally | ✗ traces page pays for a payload it never reads |
| `?includeServiceStats=true` toggling array vs object | ✗ polymorphic response; ugly to type on both ends |
| Separate `/services/stats` endpoint | ✗ a genuine second full scan |
| **New `/api/traces/overview`** | ✓ one scan, clean typing, `/histogram` untouched |

#### Backend

- New `ServiceStats` model: `service`, `count`, `errorCount`, `errorRate`, `ratePerSecond`, `avgMs`,
  `p95Ms`.
- New `TraceOverview` result: `{ Buckets: List<TraceVolumeBucket>, Services: List<ServiceStats> }`.
- `ITraceReadRepository.GetTraceOverviewAsync(HistogramQuery, ct)` — calls `ComputeTraceInfosAsync`
  **once**, then groups the result two ways: by time bucket (existing logic, extracted to a private
  helper shared with `GetTraceHistogramAsync`) and by service.
- Group by `TraceInfo.ServiceName` — the **root span's** service. That is the correct RED semantic
  ("traces originating in service X"); `Services[]` is the full participant list and would
  double-count a trace across every service it touches.
- `GET /api/traces/overview` with the same query parameters as `/histogram`.

`/api/traces/histogram` is left exactly as-is for trace-list.

#### Frontend

- Dashboard swaps its `getTraceHistogram` call for `getTraceOverview` — **not an additional call**.
- New `service-health-table` component; sortable; clicking a row sets `selectedService`, which
  subsumes the current dropdown into something scannable.

#### Future consolidation (not this phase)

`/overview` is the natural home for the dashboard's other per-load queries — the three service-list
calls in particular are already merged client-side into one `Set`. Worth folding in later; out of
scope here.

**Rejected alternative:** deriving the table client-side from the existing 500-trace sample. It is
free, but the numbers would **visibly disagree with the KPI strip directly above it**, which erodes
trust in the whole page.

**Acceptance**
- Σ per-service counts == `totalTraces()` for the same window.
- Backend scan count per dashboard load is unchanged from Phase 4 (verify by log or profiler).
- `/api/traces/histogram` response is byte-identical to before; traces page unaffected.
- Clicking a row filters the whole dashboard; clicking again clears.
- Sorting works on every column.

---

### Phase 6 — Custom metrics section (localStorage)

**Why last:** most speculative. Prove it earns its place before paying for server-side persistence.

**Config** — an array of `{ id, metricName, labelFilters, chartType, span }` persisted through the
existing `loadPageState` / `savePageState`
([page-state.ts](../src/telemetry-client/src/app/shared/utils/page-state.ts)) under the dashboard's
key. **Zero backend.**

**UI** — an "Add chart" dialog reusing the metric picker from the metrics feature; per-card remove /
resize; the Phase 3 container queries make the grid work for free.

**Reuse note:** `chart.utils.ts` is already a rich shared library (`aggregateSeries`,
`computeRateSeries`, `buildShareDonut`, `buildRadialGauge`, `aggregateHistogramQuantiles`, …). What
is *not* shared is the metric-type → builder orchestration, which lives inline in
`metric-detail.component.ts` (~lines 457–710). **Scope v1 to Gauge + Sum (counter) only** and extract
just that path; distributions (histogram / exp-histogram / summary) are a later increment.

**⚠ Refresh cost:** each chart is its own `getSeries` / `getGroupedSeries` call. Twelve saved charts
on a 30s auto-refresh is 12 requests every 30 seconds. Required mitigations:
- lazy-render on scroll into view (`IntersectionObserver`) — off-screen charts do not fetch
- a **separate, slower** refresh cadence for this section than the KPI strip

**Acceptance**
- Charts persist across reload; corrupt localStorage falls back cleanly (already handled by
  `loadPageState`).
- Only visible charts issue requests.
- Removing a chart cancels its in-flight request.

---

### Phase 7 — Server-side layout persistence (gated)

**Do not start unless Phase 6 is actually used.**

Per-user-per-tenant layouts need a `dashboard_layouts` table, which per `CLAUDE.md` means **all five
schema scripts plus `TARGET_VERSION` in `apply-schema.sh`, in one commit**, plus CRUD across the
provider repositories. Real work; not to be taken on speculatively.

If it happens, it is also the natural home for a per-tenant error-rate threshold (Decision 5).

---

## 4. Summary

| Phase | Scope | New HTTP | New scans | Backend |
|---|---|---|---|---|
| 1 — Drop log fetch | client | **−1** | −1 | no |
| 2 — RED KPIs + sparklines | client | 0 | 0 | no |
| 3 — Responsive layout | client + `styles.scss` | 0 | 0 | no |
| 4 — Percentiles | Core + client | 0 | 0 | yes, small |
| 5 — Service health + overview endpoint | Core + API + client | 0 | **0** | yes |
| 6 — Custom metrics | client | +N lazy | 0 | no |
| 7 — Layout persistence | schema ×5 + providers | — | — | yes, large |

**Phases 1–3 deliver most of the value and touch only the Angular client.** Phase 4 is small and
disproportionately valuable. Phase 5 adds the highest-value new widget at no scan cost. Phases 6–7
are optional.

---

## 5. Decisions

All five previously-open decisions are resolved.

### Decision 1 — Card labels: **accurate, with tooltips**

`Traces/sec` · `Avg Trace Duration` · `p95 Trace Duration`.

The deciding factor is not pedantry: **this tool ingests arbitrary OTLP, not just HTTP.** Anyone
instrumenting a queue consumer, scheduled job, or batch worker produces traces that are not requests
and have no "response". "Req/s" would be actively wrong for them, and quietly wrong for anyone whose
traces fan out to async work.

Tooltip: *"Wall time of the root span across the whole trace, including async work."*

*Considered and rejected:* filtering roots to `SpanKind.Server` (≈ inbound requests). `TraceInfo`
does not expose root span kind — only `rootSpanAttributes` — so it is backend work, and it would
silently drop non-server traces from the headline numbers. Revisit only if a distinct "requests"
view is ever wanted alongside "traces".

### Decision 2 — Log signal: **keep exactly one card, make it clickable**

An `Error Logs` card summing `error + fatal` from `logHist`. Free, and it catches failures that never
produced an error span — startup crashes, background jobs, uninstrumented paths. Full severity
breakdown stays on the Logs page.

- **No threshold coloring.** Most systems log errors continuously; a permanently-red card is a card
  nobody reads. The sparkline carries the signal — a step change in shape is the actual alarm.
- **Click → `/logs?severity=17`.** This is what makes it triage rather than decoration, and per §2.7
  it needs **zero changes to the Logs page** — the param is already supported and `17` is already its
  Error option, with `minSeverity` (>=) semantics that match the card's error+fatal sum exactly.

### Decision 3 — Phase 5 scan cost: **fold into a new `/overview` endpoint**

`ComputeTraceInfosAsync` already materializes the exact list service stats needs; grouping it a
second way is free. A dedicated `/api/traces/overview` gives one scan, clean typing, and leaves
`/histogram` untouched for trace-list. Full option comparison in Phase 5.

This supersedes the original plan's framing of Phase 5 as "the first real cost decision" — there is
no longer a cost to decide about.

### Decision 4 — Widescreen cap: **2400px as a CSS custom property, no user preference**

A preference means a settings UI, persistence, docs, and a support surface for marginal benefit. The
Phase 3 container-query approach already makes widgets adapt to their own width, so the cap matters
less than it would with viewport media queries.

`--dashboard-max-width: 2400px` gives a one-line override in a theme or user stylesheet — most of the
flexibility of a preference, none of the build cost. Promoting it to a real setting later is trivial
if complaints arrive.

### Decision 5 — Error-rate threshold: **two hardcoded tiers, not derived from alert rules**

`ERROR_RATE_WARN = 0.01`, `ERROR_RATE_ERROR = 0.05` as named constants (see Phase 2).

*Considered and rejected:* deriving from alert rules. They are per-tenant and per-rule-type, each
with its own condition, window, and often a specific service scope. Mapping an arbitrary set of
`ErrorRate` rules onto one global card is ambiguous — given rules at 2%, 5% and 10% scoped to
different services, which colors the card? That invents a resolution policy nobody asked for and
couples the dashboard to the alerting schema.

The clean separation: **alerts own "is this a violation"; the KPI card owns "does this look off at a
glance."** Different jobs, different thresholds. A per-tenant threshold gets a natural home in
Phase 7 if that ever lands.

---

## 6. Risks

| Risk | Mitigation |
|---|---|
| Phase 1 changes a displayed number | Intentional correction; note in commit message |
| Phase 2 card link drifts from card value | Acceptance test asserts the two counts match |
| Phase 3 touches shared `styles.scss` | Dashboard-scoped modifier; `.chart-grid` is dashboard-only |
| Phase 4 must hold for all 5 providers | Shared base class, no SQL; spot-check two |
| Phase 5 refactor regresses `/histogram` | Extract shared private helper; assert byte-identical response |
| Phase 6 request storm | Lazy render + separate cadence, both required not optional |
| No .NET test projects exist | Manual verification steps are the acceptance criteria above |
