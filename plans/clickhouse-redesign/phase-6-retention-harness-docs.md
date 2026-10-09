# Phase 6: retention, test and harness cleanup, documentation

Part of the [ClickHouse redesign](README.md). Retention on the new tables, removal of what the old layout left in the
tests and the stress harness, and the documentation for the new provider.

## Decisions

| # | Decision |
|---|---|
| 1 | Retention stays `ALTER TABLE ... DROP PARTITION ID` per fully expired day, rows counted from `system.parts` first (row model decision 17): traces drop `spans`, `trace_index`, `request_rollup_minute`; logs drop `log_records`, `log_rollup_minute`; metrics drop the five points tables. Only the raw tables' rows are counted, as today |
| 2 | The metric sweep also runs one lightweight `DELETE FROM metric_series WHERE last_seen < cutoff` and the same on `metric_catalog` (row model "Metric catalog"); their counts are not added to the sweep's total |
| 3 | `RetentionTestsBase` stays unchanged (P7); `ClickHouseRetentionTests.CountRowsAsync` maps the base's table and column names to the new ones (it already owns that SQL). Its "metrics catalog is not removed" assertion holds because seeded catalog rows are current |
| 4 | Tests that only describe the old layout are deleted: `ClickHouseDuplicateReferenceRowTests` (no reference tables), and the ClickHouse parts of `ClickHousePlanSurvey` that read the old tables are rewritten for the new ones or removed |

## Steps

1. **`ClickHouseRetentionSweeper`** (decisions 1, 2).
2. **Tests** (decisions 3, 4): `ClickHouseFixture` truncation list; `SeededDataBuilder`'s ClickHouse notes about the
   process-wide `ResourceScopeCache` (no longer relevant to ClickHouse); `DatabaseObserverTests`; `CapabilitiesTests`.
3. **Stress harness**:
   - `ClickHouseObserver.CountRowsAsync`/`ReadAsync`: new table and column names; drop `RawSpanRows` ("pending merge
     duplicates" no longer applies, spans are plain appends) or keep it at zero, whichever keeps the report model
     simple.
   - `CorrectnessChecker`: the ClickHouse retention leftovers wait (written for mutations) is no longer needed for
     partition drops; keep it only if the catalog `DELETE` is ledgered.
   - Read queries the harness and `TraceQueryBench`/`RollupQueryBench` issue against ClickHouse set
     `use_query_condition_cache = 0` (Phase 0 spike 4: the 25.x condition cache makes a repeated predicate look free);
     production queries do not.
   - `HtmlReportWriter`/`RunMetadataCollector`: ClickHouse version 25.8; report the new `late_buffer_records` and
     `derived_rows_dropped` instruments beside the write instruments.
   - `ScenarioProfile`: the ClickHouse batching flags added in the diagnosis (`clickhouse-diagnosis`) are replaced by
     `Telemetry:ClickHouse:Ingestion` settings.
4. **Documentation**:
   - `src/Keryhe.Telemetry.ClickHouse/README.md`: rewritten for the new layout (25.8, tables, write path, derived data
     and its under-count, attribute strings, metric instances per service, late data, retention).
   - `docs/CONFIGURATION.md`: the ClickHouse section.
   - `docs/SETUP.md`: 25.8; fresh install.
   - `CLAUDE.md`: "ClickHouse provider notes", "Summary rollups" (ClickHouse is written by the collector, not views),
     "`metric_last_seen`" (not on ClickHouse), "Trace detail" (`ReferenceRowsSql`/`JoinsReferenceRows` gone),
     "Retention", "Database" (ClickHouse table list), the stress-test paragraph on ClickHouse OOM at 21,000 records/s
     (replace with phase 7's number).
   - `plans/clickhouse-row-model.md`: mark as implemented, linking here.

## Tests

- `RetentionTestsBase` on ClickHouse; a new check that an expired `metric_series` row is deleted and a current one kept.
- The full integration suite on all four providers.
- The stress harness `smoke` profile on ClickHouse: report renders, correctness ledger balances.

## Done when

- `grep -ri "resources\|instrumentation_scopes\|metric_last_seen\|mv_" src/Keryhe.Telemetry.ClickHouse
  tests/**/ClickHouse*` finds nothing that refers to the old layout.
- The docs and CLAUDE.md describe the new provider and nothing of the old one.

## Implementation notes (2026-10-09)

Implemented, uncommitted. All 8 `Retention` tests, 114 ClickHouse tests, 651 non-ClickHouse tests pass; the stress `smoke`
profile on ClickHouse ends with 18 of 18 ledger cells matching and backdated rows removed.

- **Sweeper.** The old sweeper still named the old points tables and read `system.parts.partition` (the *formatted* date,
  `2026-01-01`, not `yyyymmdd`), so nothing matched. It now uses the new table names and `partition_id`. The metric sweep
  also runs `DELETE FROM metric_series / metric_catalog WHERE last_seen < cutoff` (`lightweight_deletes_sync = 1`); their
  counts are not in the total. New test `MetricRetention_DeletesExpiredSeriesAndCatalogRows_AndKeepsCurrentOnes`.
- **Bug found by the stress ledger (fixed in the worker).** `ClickHouseIngestionWorker` judged a *metric* by its first
  data point, so a metric whose first point was past retention lost its current points too (smoke: 1.5% of points
  missing, `records_dropped` 9,151). It now uses the metric's newest point; older points in a kept metric go into their own
  day's partition and age out with it. Test `AMetricIsDroppedOnlyWhenEveryPointIsOutOfRetention`.
- **Tests.** `ClickHouseRetentionTests.CountRowsAsync` maps the base's names to the new tables and columns. Fixture and
  `SeededDataBuilder` notes updated; `ClickHousePlanSurvey` needed no change (it only wraps `EXPLAIN`). The two benches read
  with `use_query_condition_cache = 0` (`ClickHouseFixture.ApiConnectionString`, only when `TRACE_BENCH_OUT` or
  `ROLLUP_BENCH_OUT` is set; the `query_log` row-count tests keep the production setting).
- **Harness.** `ClickHouseObserver.CountRowsAsync` counts the new tables directly (each carries `tenant_id`); `RawSpanRows`
  stays in the report model, always null; the 60-second mutation re-count in `CorrectnessChecker` is gone; the API host of a
  ClickHouse run gets `set_use_query_condition_cache=0`; the report charts `late_buffer_records` and
  `derived_rows_dropped`. `ScenarioProfile` had no ClickHouse batching flags left to replace.
- **Core cleanup.** `JoinsReferenceRows`, `ReferenceRowsSql`, the by-id resource/scope loaders, `ReferenceRow` and
  `SupportsSeekAnchors` are removed from `TraceReadRepositoryBase`. Many doc comments in the Core read bases still say
  "ClickHouse overrides this"; the overrides that nothing uses any more (`ResourcesTable`/`ScopesTable` expressions,
  `HintedTraceTimeBounds`, ...) were left alone and can go in a later tidy-up.
- **Docs.** ClickHouse README rewritten; `CONFIGURATION.md`, `SETUP.md`, the stress README and `CLAUDE.md` (provider notes,
  rollups, metric touch, trace detail, retention, database) describe the new provider. The OOM-at-21,000-records/s sentence
  now says it is the old layout's number; phase 7 supplies the new one. `plans/clickhouse-row-model.md` is marked implemented.
- **Not done.** The smoke report's 7 `400`s (about 1 ms each) are the raw-search window guard answering the tour's wide
  ranges, as on the other providers; nothing was checked in a real browser.
