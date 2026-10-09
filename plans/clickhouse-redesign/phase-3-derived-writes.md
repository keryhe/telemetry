# Phase 3: derived writes

Part of the [ClickHouse redesign](README.md). Fills `trace_index`, `request_rollup_minute`, `log_rollup_minute`,
`metric_catalog` and `metric_series` from the collector (row model decision 13), inside the bulk writer's flush and
after its raw insert succeeds (README R1), with no materialized views.

## Decisions

| # | Decision |
|---|---|
| 1 | Order within a trace flush: insert `spans`; on success insert `trace_index` rows and `request_rollup_minute` rows. Logs: `log_records`, then `log_rollup_minute`. Metrics: the point tables, then the catalog and series rows that are due. A raw insert that fails writes no derived rows |
| 2 | Each derived insert's token is the raw batch's token plus the table name, so a retry of the whole flush (phase 2) can neither double-count a rollup nor double a `trace_index` row's sums |
| 3 | A derived insert that fails after its raw insert succeeded is retried by itself (same token) up to `MaxFlushRetries`; if it still fails, the raw rows stay, the derived rows for that batch are lost, and `derived_rows_dropped` (tag `table`) counts them. This is the row model's accepted under-count, now also for a failure rather than only a crash |
| 4 | `trace_index`: one row per (tenant, trace, service, day) in the batch, with `start_min`, `end_max`, `span_count`, `has_error` folded in C#. Day is the span's own partition day, so a trace across midnight gets a row per day |
| 5 | `request_rollup_minute`: inbound spans only (SERVER, CONSUMER), per (tenant, service, minute, operation); bands from `DurationBands.IndexOf`, the one definition (README R5's column names). `log_rollup_minute`: per (tenant, service, minute, severity), `-1` for an unspecified severity, matching the relational meaning |
| 6 | `metric_series`: a process-wide tracker keyed by `series_id` remembers when each series was last written. A series row is written the first time it is seen, then again at most every `CatalogRefreshSeconds` (300) while it keeps reporting. `metric_catalog` rows follow the same rule keyed by the metric id (README R6). The tracker is bounded by `MaxTrackedSeries` (1,000,000, oldest evicted; an evicted series is simply re-written) |
| 7 | The folding code is provider-neutral C# where it can be: reuse `RollupAccumulator`'s per-batch fold if it can be called without its lock and without `RollupWorker` (extract a static fold method in Core if not; README R4) |

## Steps

1. **Folds**: `TraceIndexFold`, the rollup folds (decision 7), `MetricCatalogTracker` (decision 6), each a pure function
   from a batch to rows plus, for the tracker, its state.
2. **Writer**: after each raw insert, build and insert the derived rows (decisions 1 to 3); record `derived_rows_dropped`.
3. **Options**: `CatalogRefreshSeconds`, `MaxTrackedSeries` in `Telemetry:ClickHouse:Ingestion`.
4. **`ClickHouseRollupStore` and `ClickHouseMetricTouchStore`** stay no-ops (README R3); their doc comments now say the
   provider writes these tables itself.

## Tests

- **`ClickHouseRollupWriteTests` passes unchanged against `RollupWriteTestsBase`** (it already seeds through
  `ITelemetryBulkWriter` and reads with raw SQL; README R5 keeps its SQL valid). Its band-edge checks now exercise
  `DurationBands.IndexOf` on ClickHouse instead of the view's `log2` expression.
- **New `ClickHouseDerivedWriteTests`**: `trace_index` against a LINQ restatement over the seeded spans (per service,
  per day, across midnight, error flag, span count); a flush retried with the same token leaves every derived table
  unchanged; a fault-injected derived insert failure leaves raw rows, drops the derived rows and counts them; the
  catalog tracker writes a series once, again after the refresh interval, and not in between.
- Negative control: perturb the fold (for example drop the `has_error` max) and confirm the new tests fail.

## Documentation

- The provider README's write-path section (in phase 6): derived tables, the crash and failure under-count, catalog
  freshness.

## Done when

- A flush through `ITelemetryBulkWriter` (the test seeding path) and one through `ClickHouseIngestionWorker` both leave
  every derived table consistent with the raw rows.
- No materialized view exists in the schema.
