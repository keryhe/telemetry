# Phase 5: logs and metrics

Part of the [Parquet + DuckDB provider](README.md). Adds the log and point tables with their derived data to the
writer, and replaces the phase 3 stubs with `ParquetLogReadRepository`, `ParquetMetricReadRepository` and
`ParquetResourceReadRepository`. Compaction (phase 4) covers the new tables through their definitions.

## Decisions

| # | Decision |
|---|---|
| 1 | **Tables** follow the ClickHouse row model, translated: `log_records` (sorted `(service_name, time_unix_nano)`, partition time `time_unix_nano` falling back to the observed time), `gauge_points`, `sum_points`, `histogram_points`, `exp_histogram_points`, `summary_points` (sorted `(series_id, time_unix_nano)`), `log_rollup_minute` (README D8), `metric_catalog` and `metric_series` (README D9, per day) |
| 2 | **Log body**: a string body as text, any other body as its JSON text, as today. Free-text search is case-insensitive `ILIKE` over the body and attribute values (the relational form); there is no token index, so `RawSearchWindowGuard` is the bound |
| 3 | **Logs by trace id** (the trace page's logs): `trace_hi = ? AND trace_lo = ?` (README D3) over the log files of the hours given by the trace's bounds from `trace_index` (or the hint), as trace detail reads spans |
| 4 | **Log lists** read in widening time slices, like the trace list, with the same `limit + 1` cap and `order`; facets under `TimedQuery` |
| 5 | **Metric ids and catalog**: a metric's id is the hash of (tenant, service, name, type) (ClickHouse R6); a series id hashes the metric id and the point attributes in key order. The catalog aggregates `metric_catalog` over the days in the metrics window, newest first-seen first, capped at the catalog limit; "has data in range" uses each row's last-seen, with no per-candidate fallback |
| 6 | **Series math** is Core's `MetricSeriesPipeline` (shared with every provider), fed by a DuckDB statement that collapses points to (stream, bucket) rows, as ClickHouse does; the `Min`/`Max` estimates and `MinMaxApproximate` follow from the pipeline unchanged |
| 7 | **Exemplars** are `LIST(STRUCT(...))` on the four point tables that have them (staged as JSON text and converted in the `COPY`, phase 0 spike 1); the exemplars list reads them newest first, capped |
| 8 | **Resources**: `ParquetResourceReadRepository` reads distinct service and resource attribute sets from the span, log and catalog tables within the window (there is no resources table), as `ClickHouseResourceReadRepository` does |
| 9 | Log rollup rows and catalog/series rows are written after the raw files, under the same retry and `derived_rows_dropped` rule as phase 3 decision 1 |

## Steps

1. Table definitions and row builders for logs and the five point types; derived rows.
2. `ParquetLogReadRepository` following `ClickHouseLogReadRepository`.
3. `ParquetMetricReadRepository` following `ClickHouseMetricReadRepository`.
4. `ParquetResourceReadRepository`; remove the stubs.

## Tests

- The remaining shared bases on Parquet: `LogPhase2TestsBase`, `MetricPhase4TestsBase`, `MetricPhase5TestsBase`, the
  log half of `RollupWriteTestsBase`, `ExportTestsBase` (logs, metrics), `SearchParityTestsBase` (logs),
  `PoisonRecordTestsBase` (logs, points), `BaselineCharacterizationTestsBase`.
- `ParquetDerivedWriteTests` extended: log rollup, catalog and series rows against a LINQ restatement.
- The phase 4 compaction equivalence tests extended to the new tables.
- The test data generator's sink tests are unaffected (they use a fake collector).

## Done when

Every page of the UI works on Parquet end to end, and every shared suite passes on Parquet with the other providers
unchanged.

## Implementation note (2026-10-10, uncommitted)

Done: tables `log_records`, `log_rollup_minute`, `gauge/sum/histogram/exp_histogram/summary_points`, `metric_catalog`,
`metric_series` (`ParquetTables`); `ParquetBulkWriter.Logs.cs` / `.Metrics.cs` (the writer is now `partial`; catalog and series rows are
tracked per series and written at most every `CatalogRefreshSeconds`); `ParquetLogReadRepository`, `ParquetMetricReadRepository`,
`ParquetResourceReadRepository`; the log half of `ParquetRollupReadRepository`; compaction merge kind `Any` (labels, unit,
description of a series or metric).

Tests: `LogMetricWriteTests` (4: log times and ids, all five point types with exemplars/buckets/quantiles, catalog refresh, compaction
of logs/points/catalog/series); the integration suite on Parquet, including the compacted fixture, 153 pass: `LogPhase2TestsBase`,
`MetricPhase4TestsBase`, `MetricPhase5TestsBase`, `BaselineCharacterizationTestsBase` newly, and the log/metric halves of the
Export, SearchParity, SummaryTimeout, RollupWrite and Retention bases that phase 3 had skipped.

Differences and findings:
- **Point sort key** is `(service_name, metric_name, series_id, time_unix_nano)` (decision 1 said `(series_id, time)`): a read names the
  service and metric first, so row groups prune on them.
- **Nested columns** (exemplars, bucket counts, bounds, quantiles, labels) are all staged as JSON text and read back with `to_json(col)`;
  no driver mapping of lists or structs. A bucket-count array is summed element by element in SQL (`unnest` with an index), as
  ClickHouse's `sumForEach`.
- **Baseline and "latest" windows are bounded to a day.** A cumulative series' first bucket is measured against its last point in
  the day before the window (ClickHouse reads back to the beginning of time); `GetLatestMetricValues` reads the last day. The
  catalog and the services list read every day of their small derived tables.
- **Seen in range** is the folded first/last seen overlapping the window, always (decision 5, no per-candidate fallback); it is
  accurate to `CatalogRefreshSeconds`.
- **Logs by trace id** read the trace's hours from `trace_index` (+-5 minutes); a trace the index has no row for (logs without spans)
  is searched for over every hour the tenant has.
- **Resources** list the services of `trace_index`, `log_rollup_minute` and `metric_catalog`.
- **Not run on Parquet:** `PoisonRecordTestsBase` (it needs a column length limit; bisection is covered in `IngestionWorkerTests`).
  The writer's tracker is reset by the test fixtures' `ResetAsync` (like ClickHouse's), since they delete the files.
- **Not done:** the live check of every logs and metrics page in the browser with the test data generator.
