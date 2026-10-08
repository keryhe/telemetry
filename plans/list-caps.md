# Capped lists instead of paging

Written 2026-10-08. The trace list, logs list, metrics catalog and a metric's exemplars stop paging with keyset
cursors. Each returns the newest (or oldest) N rows for the filters and says when there were more; the user narrows the
time range or filters to see others, and export still streams everything. This removes the cursors, the `asOf` pin and
the columns that exist only for them, on the API, the UI and every provider.

**Order of work:** after `plans/control-plane-split.md` (Timescale is gone by then and schema 4.0.0 is already open) and
before the ClickHouse redesign (`plans/clickhouse-row-model.md`), which assumes capped lists. If this plan runs before
the split instead, every step that names the relational providers also covers Timescale.

## Decisions

| # | Decision |
|---|---|
| 1 | Trace list, logs list, metrics catalog and exemplars return at most N rows. No cursors, no `nav`, no page sizes |
| 2 | Defaults: logs 1,000, traces 500, metrics catalog 500, exemplars 500. Configurable (`Telemetry:Query:Limits`); a request may ask for fewer, never more |
| 3 | Each list response carries `truncated: true` when more rows matched (the query fetches N + 1). The UI says so and suggests narrowing |
| 4 | Logs and traces take `order=newest\|oldest` (default `newest`) so both ends of a window are reachable. The catalog keeps its current ordering (most recently seen first); exemplars are newest first |
| 5 | No `asOf` pin anywhere. A list is one request, so there's nothing to keep stable between requests |
| 6 | Schema 4.0.0 drops `created_at` from `spans` and `log_records` on the relational providers. It stays on `metrics` (it means "first seen", which the catalog sorts and filters on), `resources`, `instrumentation_scopes` and the control-plane tables |
| 7 | No "load older" button yet. Add it later if people miss it: re-query with the window ending at the oldest row shown |
| 8 | Breaking API change: `GET .../logs/page` and `GET .../traces/page` become `GET .../logs/list` and `GET .../traces/list`, so an old client sending a cursor gets a 404 instead of silently receiving the first rows again |
| 9 | `ProviderCapabilities` drops `ExemplarPaging` and `AsOfBackoffSeconds` and gains the four limits, so the UI can print "Showing the newest 1,000" |
| 10 | Dead code goes in the same change: `GET .../logs` (an unbounded time-range read the UI no longer calls), `ILogReadRepository.GetLogRecordsByTimeRangeAsync` and `GetLogRecordByIdAsync` (no caller) |

What stays the same:
- The relational `id` columns on spans, logs and data points. They're no longer a paging tiebreak, but SQL Server's
  clustered keys and MySQL's primary keys depend on them for uniqueness. Removing them would be its own change.
- The trace-list anchor rule: one row per trace, the earliest span in scope.
- The seek-anchor slices on PostgreSQL, SQL Server and MySQL: they exist so a list doesn't scan the whole window.
- The rollup cards and charts, trace detail, logs-by-trace, log context and export.

## API

| Endpoint | Before | After |
|---|---|---|
| `GET .../traces/page` | `start, end, asOf, mode, service, operation, minDurationMs, maxDurationMs, q, size, cursor, nav` → `{items, nextCursor, prevCursor, asOf}` | `GET .../traces/list`: `start, end, mode, service, operation, minDurationMs, maxDurationMs, q, order, limit` → `{items, truncated}` |
| `GET .../logs/page` | `start, end, asOf, service, minSeverity, q, size, cursor, nav` → `{items, nextCursor, prevCursor, asOf}` | `GET .../logs/list`: `start, end, service, minSeverity, q, order, limit` → `{items, truncated}` |
| `GET .../metrics/catalog` | `..., size, cursor, nav` → page with cursors and totals | `..., limit` → `{items, truncated}` |
| `GET .../metrics/exemplars` | Keyset paging, or the newest 500 when `ExemplarPaging` is false | `..., limit` → `{items, truncated}`, newest first, on every provider |
| `GET .../logs` | Every log in the window, uncapped | Removed |
| `GET /api/capabilities` | `exemplarPaging`, `asOfBackoffSeconds`, ... | Those two removed; `logListLimit`, `traceListLimit`, `metricCatalogLimit`, `exemplarLimit` added |

`limit` is clamped to `[1, configured limit]`. Facets, samples, summaries, trace detail, by-trace, context and export
are unchanged.

## Phases

### Phase 1: Core and API

1. **Query models** (`Core/Models/Query.cs`, `Metrics.cs`, `MetricSeriesModels.cs`): `LogQuery` and `TraceQuery` lose
   `Size`, `Cursor`, `Nav` and `AsOf`, and gain `Limit` and `Order`. Rename `LogPageResult` / `TracePageResult` to
   `LogListResult` / `TraceListResult` with `Items` and `Truncated`. The catalog and exemplar queries and results
   change the same way.
2. **Limits options:** a `QueryLimitsOptions` class bound from `Telemetry:Query:Limits` (`Logs`, `Traces`,
   `MetricCatalog`, `Exemplars`), validated as positive at startup. Document them in `docs/CONFIGURATION.md`.
3. **Delete** `KeysetCursor`, `NameKeysetCursor`, `DapperReadRepository.ResolveAsOfAsync`, `DatabaseClockNowExpr`,
   `PostgresAsOfBackoffSeconds` and every provider override of them.
4. **`LogReadRepositoryBase`:** `GetLogListAsync` replaces `GetLogPageAsync`. One query: the filters, then
   `ORDER BY time_unix_nano DESC, id DESC` (or `ASC` for oldest), then `LIMIT limit + 1`. `truncated` is whether the
   extra row came back.
5. **`TraceReadRepositoryBase`:** `GetTraceListAsync` replaces `GetTracePageAsync`.
   - `FetchSlicedAnchorPageAsync` keeps its slicing. It starts from the window's end for `newest` or its start for
     `oldest`, widens until it has `limit + 1` anchors, and loses the cursor predicate.
   - `AnchorsSql`, `SeekAnchorsSql` and `WindowAnchorsSql` lose `pinAsOf` and the `created_at <= @asOf` predicates.
   - The follow-up query (`LoadPageTraceInfosAsync`: span counts, whole-trace bounds, error flag) and `ErrorScope` lose
     the pin.
   - Errors and slow modes keep their current shape and gain `order`.
6. **`MetricReadRepositoryBase`:**
   - Both catalog views (by name, by instance) return the first `limit + 1` rows in their current order. That removes
     the cursor encoding and the exact-count queries (`TryGetExact*CountAsync`).
   - Exemplars: one newest-first path with `limit + 1` on every provider. `GetMetricExemplarsCappedAsync` becomes the
     only implementation, and the keyset path and `CountExemplarsAsync` go.
7. **Controllers** (`LogsController`, `TracesController`, `MetricsController`, `CapabilitiesController`): routes and
   parameters as in the API table; remove `GET logs`. `ProviderCapabilities` as in decision 9; providers stop setting
   `AsOfBackoffSeconds` and `ExemplarPaging`.
8. **Export** keeps its own uncapped streaming. Check that each export's filter compilation no longer references
   `AsOf`.

### Phase 2: Providers

For each provider, remove its overrides of the deleted members and its pin helpers.
- **PostgreSQL:** remove `AsOfBackoffSeconds` registration.
- **SQL Server:** remove `SqlServerReadRepositories`' pin helpers and its `SYSDATETIME()` clock override.
- **MySQL:** remove the `CURRENT_TIMESTAMP(6)` clock override and its pin helpers.
- **ClickHouse (current provider):** remove the `now64(9)` clock override, the `created_at <= @asOf` pin and
  `anchor_created_at` in its anchors SQL, and its exemplar override if it only existed for the capped tier. Its
  `spans` and `log_records` `created_at` columns go in the schema change below. The current ClickHouse provider is
  replaced by the redesign later, so these changes keep it working with the minimum of edits.

### Phase 3: Schema 4.0.0

- Drop `created_at` from `spans` and `log_records` in `PostgreSQL-Telemetry.sql`, `SqlServer-Telemetry.sql`,
  `MySQL-Telemetry.sql` and `ClickHouse-Telemetry.sql` (the file names after the control-plane split). Check that no
  index includes it.
- The version stays 4.0.0 (still unreleased and fresh-install only, `schema-versioning`). This change lands in the same
  release as the split.
- The bulk writers don't list `created_at` for those tables (it was a default); confirm and remove any explicit
  mention.

### Phase 4: UI

- **API services** (`logs-api.service.ts`, `traces-api.service.ts`, `metrics-api.service.ts`):
  - Calls go to the new routes with `order` and an optional `limit`.
  - Remove `asOf`, `cursor`, `nav` and `size`, and the `asOf` handling notes.
  - Remove `getLogs`.
- **Trace list and logs page:**
  - Remove `mat-paginator`, `GroupedPaginatorIntl`'s "of many" handling, `pageIndex`, the page-size options and the
    `size` URL parameter.
  - Add a Newest / Oldest toggle, kept in the URL as `order`.
  - When `truncated`, show a line above the table: "Showing the newest 1,000 matching logs. Narrow the time range or
    add filters to see others." Use the oldest wording when `order=oldest`, and take the number from capabilities.
  - Keep the table virtualized or plain according to row count. 1,000 rows must stay responsive, so check render time
    and add `cdk-virtual-scroll` only if it's needed.
- **Metric list:** remove its paginator; show the truncation line when `truncated`.
- **Metric detail exemplars:** remove the paginator and `exemplarsTotal`; show the truncation line.
- **Clean-up:**
  - `shared/utils/paginator-intl.ts` goes if nothing else uses it.
  - `CapabilitiesService` reads the four limits and drops `exemplarPaging`.
  - Saved list state (`saved.pageSize`) goes.
- **The logs page doesn't need log ids.** Rows are tracked by index, and context highlighting uses the timestamp plus
  the row. Confirm nothing uses `LogRecord.id`, so the ClickHouse redesign can omit it.

### Phase 5: Tests and harness

- **Delete** `KeysetCursorTests`.
- **Rework** to the list contract, keeping their negative controls:
  - `LogPhase2TestsBase` (filters, both orders, `truncated` at exactly N and N + 1 rows).
  - `TracePhase3TestsBase`: anchor semantics. `SlicedPaging_MatchesTheAnchorDefinition_...` becomes "the list matches
    the definition restated in LINQ for every filter, order, limit and slice width".
  - `MetricPhase4TestsBase` and `MetricPhase5TestsBase` (catalog and exemplars).
  - `SearchParityTestsBase`, `ScenarioTests` and `PlanSurveyTests`, wherever they page.
  - `CapabilitiesTests` (new shape).
  - `ClickHouseDuplicateReferenceRowTests` and `SqlServerSchemaTests`, wherever they call the page methods.
- **ApiHttp:** new parameter validation (`order`, `limit` clamping), 404 for the old `page` routes, and the removed
  `GET logs`. The authorization test that enumerates every action keeps passing with the renamed actions.
- **Stress harness:**
  - `MarkerProbe` queries `logs/list?...&limit=1`.
  - Remove the `asOf` pin offset: `ScenarioRunner.ReadAsOfBackoffMsAsync`, `RampEvaluator`'s `LogLagsMs` adjustment and
    the `HtmlReportWriter` note. The log lag is then measured directly.
  - The browser tour clicks the order toggle instead of paging.
- **`TraceQueryBench`:** time `list newest` and `list oldest` instead of first/next page.

### Phase 6: Docs

- **CLAUDE.md:**
  - The "Query layer" paragraph: no keyset, no `asOf`; capped lists, `truncated`, `order`.
  - Remove the `asOf` / "new since" sentences and the `AsOfBackoffSeconds` mentions.
  - "Trace anchors": no pin, slices fill `limit + 1`.
  - The capabilities list.
- **Other docs:**
  - `docs/CONFIGURATION.md`: the limits.
  - The API README: breaking-change note with the old→new routes.
  - The stress-test README: the probe and the pin offset.

## Verification

1. `dotnet build Telemetry.sln` and the Angular build.
2. Unit tests without Docker: `--filter Suite=ApiHttp`, `Suite=CollectorAuth`, rollup unit tests.
3. Integration tests per provider: `--filter Provider=PostgreSQL`, then `SqlServer`, `MySql`, `ClickHouse`.
4. Manual UI check against PostgreSQL with the test data generator:
   - logs and traces in both orders, with the truncation line shown and hidden;
   - the metric list and exemplars;
   - deep links with `order` in the URL.
5. A stress `smoke` run per provider, to confirm the probe and report changes.
6. A `TraceQueryBench` run on PostgreSQL and MySQL, comparing list-newest with the old first page. They should match
   within noise; `limit` is larger than the old default page size of 100, so a little slower is expected.

## Risks

- **Busy tenants see a short slice.** At 1,000 logs/s, the newest 1,000 logs is about one second. The truncation line
  has to make narrowing obvious, and search and filters do the real work. "Load older" is the follow-up if this
  proves frustrating.
- **Larger responses.** 1,000 log rows, or 500 trace rows each with follow-up lookups, cost more per request than the
  old 100-row page. The bench and smoke runs check this.
- **External API consumers break** (decision 8). This is the same release as the 4.0.0 schema and the split, which
  are already breaking.
