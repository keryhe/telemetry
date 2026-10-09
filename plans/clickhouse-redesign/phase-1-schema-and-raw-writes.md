# Phase 1: schema and raw writes

Part of the [ClickHouse redesign](README.md). Replaces `schema/ClickHouse-Telemetry.sql` with the row model's layout
(P3: it stays 4.0.0) and rewrites `ClickHouseBulkWriter` so spans, log records and the five point tables are written
in it. Derived tables are created here but filled in phase 3; batching changes in phase 2.

## Decisions

| # | Decision |
|---|---|
| 1 | The script is rewritten, not edited: `schema/ClickHouse-Telemetry.sql` becomes Phase 0's `schema.sql` plus the `telemetry_schema_version` row (4.0.0). Removed: `resources`, `instrumentation_scopes`, `metrics`, `metric_last_seen`, every materialized view, and the old `trace_index`. `TELEMETRY_TARGET_VERSION` in `apply-schema.sh` is unchanged |
| 2 | ClickHouse 25.8 LTS is the supported version: the test container image, the stress harness image, docs. 24.8 is dropped |
| 3 | `ClickHouseIds` keeps only what the new layout needs: the hex/`Guid`/`UInt64` id helpers (README R8), `series_id` (64-bit hash, phase 3 uses it too) and the metric id hash (README R6). `FromHash`, `FromKey` and `RowId` go |
| 4 | Attribute conversion (`AnyValue` to map string, row model "Conventions") is one static class, `ClickHouseAttributes`, used by every table's row builder, so spans, logs, points and resources encode identically |
| 5 | `ResourceScopeCache` is not used by the ClickHouse writer; resources and scopes are copied onto rows. Converting a resource's attribute map is memoized per `ResourceModel` instance for the length of one flush (the gRPC services share one instance per `ResourceSpans` block), mirroring `CachedHash` |
| 6 | One long-lived `ClickHouseBulkCopy` per destination table stays (today's `TableBulkCopy` cache); its doc comment carries over |
| 7 | A record whose `service.name` is absent is written with `service_name = ''`, as today; `tenant_id` comes from the existing `TenantAndService` helper |

## Steps

1. **Schema.** Write the new script from Phase 0's DDL: `spans`, `log_records`, `gauge_points`, `sum_points`,
   `histogram_points`, `exp_histogram_points`, `summary_points`, `trace_index`, `request_rollup_minute`,
   `log_rollup_minute` (README R5's column names), `metric_catalog`, `metric_series`, `telemetry_schema_version`.
   Each table's header comment says what reads it and why its sort key is what it is (the row model's reasoning, cut
   short).
2. **Row builders.** In `ClickHouseBulkWriter`, replace the current row arrays:
   - spans: trace id as `Guid`, span and parent ids as `ulong` (0 for a root), `start_time` as a `DateTime` (100 ns, README R9), `duration_ns = max(0, end - start)`, events and links as
     `Nested` arrays, resource and scope columns from the span's resolved resource/scope (already resolved by
     `TraceWriteRepository`);
   - logs: `timestamp` falls back to `observed_timestamp`; a non-string body as its JSON text; absent trace/span id as
     `Guid.Empty` / 0;
   - points: one table per type; `series_id` computed per point (decision 3); `as_int` stored as `Float64`; exemplars
     as `Nested`; summary quantiles as `Nested`.
3. **Remove the reference-table path**: `ResolveResourcesAsync`, `ResolveScopesAsync`, the metric catalog upsert and
   their cache use.
4. **Token plumbing (README R2).** Add `FlushTracesAsync(spans, token, ct)` and the log/metric equivalents; each
   insert sets `insert_deduplication_token` (through the bulk-copy connection's `CustomSettings`) to `token` plus the
   table name (one batch writes several tables). **`ClickHouseBulkCopy.BatchSize` is set to at least the batch's row
   count** (Phase 0 spike 3: every extra INSERT statement under one token is silently dropped); a batch above
   `MaxBatchRecords` is split by the caller into separately tokened batches, never by the bulk copy. The
   `ITelemetryBulkWriter` overloads call them with `Guid.NewGuid()`.
5. **Test container.** `ClickHouseProviderContainer.ImageName` to 25.8; the stress harness's ClickHouse image setting
   likewise. `ClickHouseFixture`'s truncate list follows the new table names.

## Tests

- **New `ClickHouseRawWriteTests`** (Testcontainers, ClickHouse only): flush a seeded set through `ITelemetryBulkWriter`
  and read every column back with raw SQL: ids round-trip, nanosecond timestamps exact, maps hold every attribute with
  the row model's value encoding (string, int, double, bool, array, kvlist, bytes), events/links/exemplars intact,
  promoted columns filled from the map including the fallback keys, `series_id` stable under attribute key order; ids
  with the high bit set round-trip as `Guid`/`ulong`; timestamps round-trip to 100 ns.
- **Dedup:** the same batch flushed twice with the same token stores once; with different tokens, twice; a batch larger
  than the old default `BatchSize` (100,000) is stored whole under one token (the spike 3 trap, as a regression test).
- The shared read-path suites are expected to fail on ClickHouse until phases 4 and 5; run them with
  `--filter Provider!=ClickHouse` meanwhile and confirm the other providers are unaffected.

## Documentation

- `schema/ClickHouse-Telemetry.sql` header: 25.8 required, fresh install only.
- Defer README and CLAUDE.md rewrites to phase 6; note the branch state in the provider README's first line if anyone
  else builds the branch.

## Done when

- `apply-schema.sh telemetry clickhouse` creates the new layout on 25.8.
- A flush through `ITelemetryBulkWriter` writes every raw table, and `ClickHouseRawWriteTests` passes.
- No code in `Keryhe.Telemetry.ClickHouse` references `resources`, `instrumentation_scopes`, `metrics`,
  `metric_last_seen` or `ResourceScopeCache`.
