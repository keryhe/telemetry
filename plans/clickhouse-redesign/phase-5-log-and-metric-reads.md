# Phase 5: log and metric reads

Part of the [ClickHouse redesign](README.md). New `ClickHouseLogReadRepository`, `ClickHouseMetricReadRepository` and
`ClickHouseResourceReadRepository`, implementing their Core interfaces directly (README R4). After this phase every API
route works on ClickHouse.

## Decisions

### Logs

| # | Decision |
|---|---|
| 1 | The list reads in **widening slices** (Phase 0 spike 5): 5 minutes first, x4 each time, the whole remainder once the next slice would cover half of it, newest end first (or oldest, for `order=oldest`). Each slice is one query `ORDER BY toStartOfFiveMinutes(timestamp) DIR, timestamp DIR LIMIT <limit + 1 - rows so far>`, stopping when `limit + 1` rows are collected. Measured: 8 to 27 ms for all-services, service, severity, common-word and service+severity filters; about 300 ms for a rare word, which must scan the window. One ordered query over the whole window read 66 to 97% of the window under a selective filter, and a fixed per-bucket loop took 75 queries for the rare word |
| 1a | **Every logs query with a time range** (lists, surrounding records, facets, exports, counts) adds `toStartOfFiveMinutes(timestamp) >= toStartOfFiveMinutes(@start) AND toStartOfFiveMinutes(timestamp) <= toStartOfFiveMinutes(@end)` beside the `timestamp` bounds. Without it the key does not prune: a 1-hour window read the whole table (3.86M rows) instead of 188k. A `LogQueryBuilder` helper adds it in one place |
| 2 | Filters: service (one short range per five-minute bucket), minimum severity (`severity_number`, minmax index), trace id (bloom filter), whole-word search through the `lower(body)` token index **written as `hasToken(lower(body), 'word')` (a `LIKE` never uses it)**, substring search and `key:value` over the 24 h raw-search window (`RawSearchWindowGuard` unchanged). The search compiler decides word versus substring from the term (letters and digits only is a word) |
| 3 | Rows have no id (row model decision 10). Where `LogRecordModel` carries an id that the API returns, the repository fills a value derived from the row (a hash of tenant, timestamp, service, trace/span id and body) so it is stable between two reads of the same row; nothing looks a log up by it (list-caps Phase 4) |
| 4 | `GetSurroundingLogRecordsAsync`: two capped ordered reads either side of the anchor time, same shape as the list |
| 5 | `GetLogFacetsAsync`: under `TimedQuery` as today; severity and service counts in the window from `log_rollup_minute` where the facet has no search term, from `log_records` where it does |
| 6 | `ExportLogsAsync`: one unbuffered ordered read, `CommandTimeout = 0`, cancellation reaching the command, as CLAUDE.md "Export" describes |

### Metrics

| # | Decision |
|---|---|
| 7 | Catalog (`groupBy=instance` and `groupBy=name`): `metric_catalog`, "seen in range" from its `first_seen`/`last_seen` (accurate to `CatalogRefreshSeconds`). The exact fallback that relational providers use when the window ended over an hour ago becomes an existence check on the type's points table by `(tenant, service, metric_name)` and time; ids per README R6 |
| 8 | Series: one points table (chosen by the catalog row's type), `GROUP BY series_id` per bucket, attribute maps fetched from `metric_series` for the series returned. Per-stream math (rates from cumulative sums, resets), top-N and "other" folding, and the cumulative histogram min/max estimate are the shared C# pipeline, extracted from `MetricReadRepositoryBase` into Core (README R4) so the two providers cannot drift. `MetricId` narrows to the service (README R6); label filters are map predicates. `TimedQuery` and the quarter-points retry as today |
| 9 | Exemplars: the `exemplars` `Nested` columns `ARRAY JOIN`ed, newest first, capped at `limit + 1` |
| 10 | Labels (`GetMetricLabelsAsync`): keys and values from `metric_series` for the metric in range |
| 11 | `GetMetricByIdAsync`, `GetMetricsByNameAsync`, `GetMetricsByTypeAsync`, `GetUniqueMetricNamesAsync`, `GetMetricCountsByTypeAsync`, `GetMetricsSummaryAsync`, `GetLatestMetricValuesAsync`: from `metric_catalog`, with `DataPointCount` (where a method returns it) a `count()` over the type's points table in the window |
| 12 | `ExportMetricSeriesAsync`: the series pipeline with `Top = int.MaxValue`, as today |

### Resources

| # | Decision |
|---|---|
| 13 | `IResourceReadRepository`: distinct services and resource attribute sets from the catalog/`metric_series` and recent `spans`/`log_records` heads, whatever the interface's current callers need. Read the callers first; this is small |

## Steps

1. **Shared helpers (README R4)**: extract the metric series pipeline (per-stream math, folding, estimates), log row
   shaping and export writers' row mapping from the bases into Core statics; the relational bases call them, behaviour
   unchanged.
2. **Log repository** (decisions 1 to 6).
3. **Metric repository** (decisions 7 to 12).
4. **Resource repository** (decision 13).
5. **Remove** `ClickHouseJsonAttributeHooks` and every remaining override of the old bases.

## Tests

All shared bases on ClickHouse, unchanged (P7): `LogPhase2TestsBase`, `MetricPhase4TestsBase`, `MetricPhase5TestsBase`,
`SearchParityTestsBase`, `ExportTestsBase`, `BaselineCharacterizationTestsBase`, the list-caps HTTP tests where they
run on a provider, `TimedQueryProviderTestsBase`, `SummaryTimeoutDegradationTestsBase`. The relational providers' full
suites again, for the extraction.

Where `MetricPhase5TestsBase` asserts per-resource catalog rows (two resources of one service as two instances),
README R6 makes ClickHouse differ: record it, and give the base a provider switch for that one assertion (the
assertion stays for the relational providers), rather than weakening it everywhere.

Plus:
- **New ClickHouse-only**: the logs list reads a bounded number of rows past `limit + 1` (`system.query_log`
  `read_rows`, after `SYSTEM FLUSH LOGS`; the spike's figures are the reference: about 11k to 50k rows for the dense
  cases), both orders, with and without service and severity filters, with `use_query_condition_cache = 0`; a 1-hour
  window reads rows in proportion to the hour, not the day (the bucket predicate, as a regression test); a word search
  uses the index and a substring search does not; a metric chart over every series of a metric reads one range per hour.

## Documentation

Deferred to phase 6.

## Done when

- Every shared test passes on ClickHouse (or has a recorded, provider-switched difference from README R6/R7) and still
  passes on the other three providers.
- Every UI page works on a ClickHouse API host fed by the test data generator (checked in the browser), including
  metric detail's service filter, exemplars and export.

## Implementation notes (2026-10-09)

- **Shared pipeline (README R4).** The metric series math moved verbatim from `MetricReadRepositoryBase` into the internal static
  `MetricSeriesPipeline` (cumulative deltas with reset detection, histogram and exp-histogram deltas, the min/max estimates,
  display-series merging, top-N, "other"); the base calls it through `using static`. Core exposes its internals to
  `Keryhe.Telemetry.ClickHouse` and the integration tests. The three relational providers' full suites pass unchanged (651 tests).
- **Both sort keys need an explicit bucket bound.** Logs: `toStartOfFiveMinutes(timestamp)` (spike 5). **Metrics too:**
  `toStartOfHour(time)`; a bound on `time` alone read the whole table (86,400 of 86,400 rows for one hour of one metric). Every metric
  time range now carries it.
- **README R6, the one recorded difference.** `MetricPhase5TestsBase` gained `CatalogInstanceIsPerResource` (true; ClickHouse
  overrides it to false): 25 pods over 5 metric names are 5 catalog instances, and the grouped-by-name view counts services
  (a1, a2, b1 of two services = 2).
- **Search.** A free-text term of letters and digits is a whole-word search (`hasToken(lower(body), 'word')`, served by the token
  index), so `pay` no longer finds `payments`; anything else (a `-`, a dot, spaces) is a substring scan. Negated attribute
  terms keep rows whose key is absent.
- **Logs have no body type or id here.** Every body is returned as text with `AttributeType.STRING`; an unspecified severity (0) is
  returned as null. Logs by trace id scan the tenant with the `trace_id` bloom filter (no time bound); surrounding records look a day
  either side of the anchor.
- **Resource list.** Services come from the request rollup, log rollup and metric catalog (there is no resources table): a service
  that only ever sent non-inbound spans and nothing else is not listed.
- **Catalog freshness.** "Seen in range" for a window ending within the last hour uses the catalog's `first_seen`/`last_seen`
  (about `CatalogRefreshSeconds` behind); an older window checks the points table.
- **Test fixture.** `ClickHouseFixture.ResetAsync` also resets the writer's catalog/series trackers: truncating the tables made the
  tracker believe rows were still written, so a later test saw no catalog (an ordering flake until this).
- **Not done:** the browser check of every UI page against a ClickHouse API host fed by the data generator.
