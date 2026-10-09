# Phase 4: trace and rollup reads

Part of the [ClickHouse redesign](README.md). A new `ClickHouseTraceReadRepository` implementing `ITraceReadRepository`
directly (README R4) over `spans` and `trace_index`, and `ClickHouseRollupReadRepository` over the new rollup tables.
After this phase the dashboard, trace list, trace detail and the alert evaluators work on ClickHouse.

## Decisions

| # | Decision |
|---|---|
| 1 | Semantics are the shared ones in CLAUDE.md ("Trace anchors"), checked by the same tests: anchor = earliest span in scope by `(start_time, span_id)`; duration, service, operation and kind are the anchor's; the error flag is "any span in scope has ERROR"; the operation filter is the anchor's own name; search matches any span of the trace regardless of the service filter; the span count is exact (`uniqExact(span_id)`) over the page's traces; the row carries the whole trace's start and end |
| 2 | The list reads as the row model's "Trace list anchors" says: slices from the window's newest (or oldest) end, widened by `Telemetry:Query:PageSliceSeconds`/`PageSliceGrowth` (the existing options) until `limit + 1` anchors, candidates confirmed against `trace_index` for the slice's day and the day before |
| 3 | **The look-back margin.** The relational lists drop anchors starting before the window and use `AnchorLookbackMinutes`. Here the anchor is confirmed by `trace_index.start_min`, so a trace whose earliest in-scope span is before the window start is excluded, and one that began more than a day before the slice is anchored on its earliest span inside the two days. `TracePhase3TestsBase` decides whether that matches the shared definition closely enough; if a test asserts the look-back's exact behaviour, the ClickHouse implementation follows the test (P7) |
| 4 | Error flag and span count for a page's rows come from one follow-up query: `trace_index` for the error flag (`max(has_error)` over the scoped service rows) and the whole-trace bounds, `spans` within those bounds for `uniqExact(span_id)` (`trace_index.span_count` counts duplicates, so it is only a size hint) |
| 5 | Modes: `errors` filters candidates through `trace_index.has_error` before confirming; `slow` filters candidate anchors on `duration_ns` (skip index), reading the whole range in one pass as today. Both keep `RawSearchWindowGuard` |
| 6 | Trace detail: bounds from the link's `?start=&end=` hint (`HintedTraceTimeBounds`, same margin option) or else `trace_index`, then `spans` within them through the `trace_id` bloom filter; a hint that finds nothing falls back to the unhinted read. Spans that share resource and scope values share one `ResourceModel`/`InstrumentationScopeModel` instance, so the controller's `resourceIndex`/`scopeIndex` output is as compact as today |
| 7 | `GetSpanByIdAsync`, `GetSpansByParentAsync`: bounded through `trace_index` the same way |
| 8 | `GetOperationStatsAsync`, `GetOperationCountsAsync`, `GetAverageLatenciesAsync` read `request_rollup_minute` by operation (row model "What reads use"). Their numbers become inbound requests per operation, not all spans; the shared tests decide whether that is acceptable, and if one asserts all-span counts, these read `spans` instead |
| 9 | `GetServiceDependenciesAsync`: a parent/child join over `spans` in the window (child range by tenant and time, parents looked up by trace ids in the same window). It stays outside `TimedQuery`, as today |
| 10 | `CountSlowInboundSpansAsync`: `spans` with `kind IN (SERVER, CONSUMER)` and the `duration_ns` skip index, under `TimedQuery` |
| 11 | `GetTraceSamplesAsync` (dashboard slowest/errors): the same candidate machinery as the list with its mode, capped at the sample count, under `TimedQuery` |
| 12 | `ExportTracesAsync`: anchors for the whole window derived once and streamed (`GROUP BY trace_id` with `argMin` over the window, as the old provider did, since export reads the whole window anyway), with per-chunk span counts and bounds every 1,000 anchors on a second connection, as CLAUDE.md "Export" describes |
| 13 | Attribute search (`key:value`) uses the map: `attributes['k'] = 'v'` (case-insensitive through `lowerUTF8`), the attribute bloom filters doing the pruning; a resource-attribute term reads `resource_attributes`. Free text matches span name, status message and attribute values as the shared search does today |
| 14 | `ClickHouseRollupReadRepository` keeps inheriting `RollupReadRepositoryBase` (README R5); only `BucketIndexExpr`/`BigintExpr` overrides stay |

## Steps

1. **Repository skeleton** implementing `ITraceReadRepository`, with the connection factory and `TimedQuery` use.
2. **Shared helpers (README R4)**: move what this repository needs out of `TraceReadRepositoryBase` into Core statics
   without changing relational behaviour: search parsing to predicates (it is already partly in `SearchQuery`), slice
   planning, the row assembly from anchors plus follow-up, the export chunking.
3. **List and samples** (decisions 2 to 5, 11, 13).
4. **Detail, span-by-id, spans-by-parent** (decisions 6, 7).
5. **Analytics and slow count** (decisions 8 to 10).
6. **Export** (decision 12).
7. **Rollup repository** (decision 14).

## Tests

All shared bases on ClickHouse, unchanged (P7): `TracePhase3TestsBase` (`HonorsStartHint` stays `true`),
`BaselineCharacterizationTestsBase`'s trace parts, `SearchParityTestsBase`'s trace parts, `ExportTestsBase`'s traces,
`RollupWriteTestsBase`'s reads, `TimedQueryProviderTestsBase` and `SummaryTimeoutDegradationTestsBase` where they cover
traces, `CapabilitiesTests`. The relational providers' full suites run too, to prove the helper extraction changed
nothing.

Plus:
- **New ClickHouse-only**: the sliced list against a LINQ restatement over seeded spans with a trace across midnight and
  a trace whose root arrives in a later flush; a duplicated span (same batch flushed twice with different tokens) does
  not change a row or its span count; detail with a wrong hint falls back.
- `TraceQueryBench` on ClickHouse, recorded as this phase's lab number (one provider at a time).

## Documentation

Deferred to phase 6, except: any test the ClickHouse implementation could not pass without changing the meaning
(decisions 3 and 8) is written down here with the chosen behaviour before the phase is called done.

## Done when

- Every trace and rollup test in the shared bases passes on ClickHouse and still passes on the other three providers.
- The dashboard, trace list (all modes, both orders, search, operation), trace detail and alert evaluators work on a
  ClickHouse API host fed by the test data generator (checked in the browser).

## Implementation notes (2026-10-09)

Behaviour chosen where the shared tests did not decide, recorded as the phase asked:

- **Look-back (decision 3).** The shared tests pass without `AnchorLookbackMinutes` in the anchor itself: an anchor is confirmed
  against `trace_index` (looked up in the slice's day and the day before). The setting still bounds the search window of a search
  term and the parent lookup of the dependency read. A trace that began more than a day before the slice is anchored on its
  earliest span inside the two days looked up.
- **Operation reads (decision 8).** No shared test asserts all-span counts, so `GetOperationStats/Counts/AverageLatencies` read
  `request_rollup_minute`: they describe inbound requests, and the percentiles are the rollup's approximate ones. A service that only
  makes outbound calls has no operations.
- **Error flag.** The list's and the samples' `HasErrors` come from `trace_index` (any span of the trace in scope, not limited to the
  window), where the relational providers limit it to the look-back range.
- **Negated search terms.** `-key:value` excludes a span carrying the value on the span or on its resource (the relational form
  would keep a span whose resource merely lacks the key).
- **Slowest samples** read the window's longest spans through the duration index (a cap of `limit * 20`, growing x4) and keep the
  ones that are their trace's earliest.
- **Lab number** (`TraceQueryBench`, 60,000 traces over 6 h, medians): first page 60-130 ms (all/service/oldest), errors 75-156 ms,
  slow 60-188 ms, search 100-110 ms, samples 34-60 ms; detail x25 607 ms (496 ms hinted).
