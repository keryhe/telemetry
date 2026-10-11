# Phase 3: trace derived data and trace reads (milestone 1)

Part of the [Parquet + DuckDB provider](README.md). The collector writes `trace_index` and `request_rollup_minute`
after the span files; the API gets the DuckDB read engine, `ParquetTraceReadRepository` and
`ParquetRollupReadRepository`; both hosts accept `Database:Provider = "Parquet"`. After this phase the dashboard, trace
list, trace detail, the error-rate and slow-request alerts and trace export work on Parquet.

## Decisions

| # | Decision |
|---|---|
| 1 | **Derived writes follow the raw write inside the flush** (ClickHouse R1): after a buffer's span files are committed, the writer builds `trace_index` rows (tenant, trace id, service, min start, max end, has error, span count) and request rollup rows (README D8, plus `operation` as ClickHouse has it) from the same rows and commits them as files in the same partitions. A derived failure leaves the spans; it is retried with the flush's token and, past the retries, counted on `derived_rows_dropped` (the ClickHouse instrument). Shared test bases seeding through `ITelemetryBulkWriter` therefore see the derived data |
| 2 | **`trace_index` is sorted by `(trace_hi, trace_lo)`** in each file, so a lookup by id prunes on min/max stats even before compaction (phase 0 spike 2: 27 ms over a day of 24 hourly files of 21 M rows); it is what tells detail which hours to read when no hint is given. It does not carry file locations (option c was not needed) |
| 3 | **The read engine** (`ParquetQueryEngine`, API singleton): one in-memory DuckDB database for the process with `memory_limit`, `threads` and `temp_directory` from `Telemetry:Parquet:DuckDb`; `OpenAsync(scope)` returns a connection with the temp views of README D5 defined for the tables and partitions in `scope` (tenant, time range, tables). `ParquetFileResolver` builds the file lists from the manifests (README "Layout" rule) with a short in-memory cache of closed partitions' manifests, keyed by the manifest files' last-write times |
| 4 | **Timeouts** use `TimedQuery` unchanged; the deadline cancels the DuckDB command through the token (phase 0 spike 4: `OperationCanceledException` within about 10 ms of the deadline, connection still usable), and a connection a timed-out query ran on is closed, never reused (CLAUDE.md's rule) |
| 5 | **Anchors** follow the ClickHouse provider's form, not the relational seek form (DuckDB has no index to seek): time slices from the window's newest or oldest end, widened by `PageSliceSeconds`/`PageSliceGrowth` until `limit + 1` anchors, each candidate confirmed against `trace_index`'s per-service minimum start for the slice's day and the previous day. The semantics are the shared ones (CLAUDE.md "Trace anchors") and `TracePhase3TestsBase` is the contract; where ClickHouse's implementation notes recorded a difference the tests accepted, the same applies here |
| 6 | **Follow-up per page**: error flag and whole-trace bounds from `trace_index`, the exact span count as `COUNT(DISTINCT span_id)` over `spans` within those bounds |
| 7 | **Detail, span by id, spans by parent**: bounds from the `?start=&end=` hint (`TraceHintMarginMinutes`, `TraceHintEnabled`) or else `trace_index`, then the spans files for only those hours with `trace_hi = ? AND trace_lo = ?` and the start bounds: a scan of the `trace_hi` column, no bloom filters (phase 0 spike 2: about 150 ms for an hour of 36 M spans of one tenant, 158 ms unhinted end to end). A hint that finds nothing falls back to the unhinted read. One `ResourceModel`/`InstrumentationScopeModel` instance per distinct resource and scope |
| 8 | **Analytics** (operation stats/counts, average latencies) read the request rollup by operation, as ClickHouse does; **service dependencies** join child and parent spans within the window; **slow-request count** reads spans with `kind IN (SERVER, CONSUMER)` under `TimedQuery` |
| 9 | **Rollup reads** inherit `RollupReadRepositoryBase` unchanged (README D2, D5, D8), overriding only the dialect hooks (`BucketIndexExpr` as `//` integer division, `BigintExpr` as a `BIGINT` cast) |
| 10 | **Export** derives anchors once over the window (`GROUP BY trace_id` with `arg_min`) and streams them, with per-chunk counts and bounds every 1,000 anchors on a second connection; `ExportMaxWindowDays` 7 (U11) |
| 11 | **Search** is the shared form: free text as case-insensitive `ILIKE` over span name, status message and attribute values; `key:value` as `lower(attributes['k']) = lower(?)`; `RawSearchWindowGuard` and `mode=slow` limits apply unchanged |
| 12 | **Host wiring**: `Collector.Server` and `Api.Server` add a `"Parquet"` case to their `Database:Provider` switch and the error messages list it; no connection string is read for this provider (U8). `ParquetLogReadRepository`, `ParquetMetricReadRepository` and `ParquetResourceReadRepository` are registered as empty stubs (empty lists, zero summaries, `truncated: false`) until phase 5, so the UI's other pages render empty rather than failing |
| 13 | `ProviderCapabilities` for Parquet: `Default()` (7-day export, the shared raw-search window and limits) |

## Steps

1. Derived rows in `ParquetBulkWriter` (decision 1) and their table definitions.
2. `ParquetFileResolver`, `ParquetQueryEngine`.
3. `ParquetTraceReadRepository`, following `ClickHouseTraceReadRepository`'s structure method by method, translating
   its SQL to DuckDB (`argMin` → `arg_min`, `uniqExact` → `COUNT(DISTINCT ...)`, map access, `toStartOf*` → integer
   arithmetic on nanoseconds).
4. `ParquetRollupReadRepository`, stubs, `AddParquetApiServices`, host switches.
5. Test infrastructure: a `ParquetProviderContainer` (a temp directory plus a control-plane-only
   `PostgreSqlProviderContainer`, the shape `ClickHouseProviderContainer` uses) and a `ParquetFixture` with
   `Provider=Parquet`.

## Tests

- The shared bases on Parquet: `TracePhase3TestsBase`, `RollupWriteTestsBase` (trace half), `ExportTestsBase` (traces),
  `SearchParityTestsBase` (traces), `TimedQueryProviderTestsBase`, `SummaryTimeoutDegradationTestsBase`, the
  capabilities test, `PoisonRecordTestsBase` (spans). A base that asserts logs or metrics runs from phase 5.
- `ParquetDerivedWriteTests`: `trace_index` and rollup rows match a LINQ restatement of the seeded spans; a failed
  derived write is retried with the same token and counted when dropped.
- `ParquetTraceReadTests`: hinted and unhinted detail across an hour and a day boundary; a trace split over two
  collectors' files; a re-delivered span collapses in detail and counts once.
- A manual end-to-end check: both hosts and the test data generator on Parquet, every trace page and the dashboard in
  the browser.

## Done when

Milestone 1: the trace pages, dashboard, trace alerts and trace export work on Parquet end to end, and the shared trace
suites pass on Parquet with the other four providers unchanged.

## Implementation note (2026-10-10, uncommitted)

Done: derived writes in `ParquetBulkWriter` (`trace_index` sorted by trace id, `request_rollup_minute`; retried under the flush's
token, counted on `derived_rows_dropped`), `ParquetFileResolver`, `ParquetQueryEngine`, `ParquetTraceReadRepository`,
`ParquetRollupReadRepository`, `ParquetRetentionSweeper` (day folders; needed for the API host's DI, phase 6 tests it more),
placeholder log/metric/resource repositories, `AddParquetApiServices`, the `Parquet` case in `Api.Server`. Verified live: collector,
generator and API host on a scratch root; trace list, detail, samples, resources and the dashboard summary endpoints answer.

Tests: `tests/Keryhe.Telemetry.Parquet.Tests` 38 (adds `DerivedWriteTests`); `Provider=Parquet` in the integration suite 50 pass
(`ParquetFixture`, `ParquetProviderContainer` = temp directory + a PostgreSQL control plane): `TracePhase3TestsBase`,
`ExportTestsBase`, `SearchParityTestsBase`, `RollupWriteTestsBase`, `RetentionTestsBase`, `TimedQueryProviderTestsBase`,
`SummaryTimeoutDegradationTestsBase`.

Differences and findings:
- **`@name` parameters.** DuckDB binds `$name`. `NamedParameterConnection` wraps the DuckDB connection and rewrites `@name` in each
  command, so the shared rollup SQL and Dapper's list expansion run unchanged.
- **Shared test bases changed (guards only; defaults keep the other providers as they were):** `ProviderFixture.SupportsLogsAndMetrics`
  (false on Parquet until phase 5) makes the log/metric tests of the Export, SearchParity, Summary and Retention bases return early;
  `RollupWriteTestsBase.SupportsLogs` skips its log halves; its raw reads convert DuckDB's `HUGEINT` sums (`BigInteger`).
  `RollupReadRepositoryBase` gained a virtual `OpenRollupConnectionAsync(kind, query, ct)` (default: the old `OpenConnectionAsync`).
- **Not run on Parquet:** `PoisonRecordTestsBase` (it needs a column length limit; the worker's bisection is covered in
  `IngestionWorkerTests`), `BaselineCharacterization` and the log/metric bases (phase 5), `CapabilitiesTests` (provider-independent).
- **Unhinted detail** reads the tenant's whole `trace_index` (all hours it has), then only the trace's hours of spans.
- **trace_index range** for a list is the window minus a day to the window plus a day (errors mode and the extent of a trace need
  rows after the slice).
- **No manifest cache** in the resolver yet (every query reads the partitions' small manifest files); add one if the stress run shows
  it matters. Views are per connection, redefined for a wider range with `DefineAsync`.
- **Not done:** the browser check of every trace page (the endpoints were exercised with curl), the stress harness provider, the
  `Collector.Server`/`Api.Server` `appsettings` entries for Parquet.
