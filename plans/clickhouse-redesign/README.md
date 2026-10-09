# ClickHouse redesign

Written 2026-10-08 (redesign step 4). Implements the row model in [../clickhouse-row-model.md](../clickhouse-row-model.md)
against the targets in [../clickhouse-diagnosis-results.md](../clickhouse-diagnosis-results.md) ("Redesign targets"),
under that file's "Planning decisions" P1 to P8. In short: ClickHouse gets its own ingestion worker (P1); Core
interfaces, models and API responses do not change (P2); the new layout is `schema/ClickHouse-Telemetry.sql` at 4.0.0
(P3); `Keryhe.Telemetry.ClickHouse` is replaced in place (P6); the shared per-provider test bases are the contract
(P7); nothing is committed or merged automatically, and the final measurement is recorded for the user to decide on
(P8).

## Status (2026-10-09)

Phases 0 to 7 are implemented on branch `feature/clickhouse-refactor`, uncommitted (P8: the user decides on merging). Each phase
file ends with implementation notes of what was built and how it differs from the plan. The measurements against the
targets are in [results.md](results.md): 82,000-94,000 records/s write-only (the 100,000 target is not met on the
dev machine), 42,700 with the UI reading, freshness p95 4.3 s, reads within budget on the data measured. After phase 7 a
parallel flush for large batches was added (results.md, "Parallel flush"). Open items: the log facets timeouts at 6 h and 7 d,
reads over realistic logs and metrics history, the generator backfill late-data check, and tidying dead ClickHouse hooks and
comments in Core's read bases. Where a decision below differs from what was built, the phase notes say so (for example R3:
`ClickHouseRollupStore.FedByViews` stays true and the worker writes the rollups).

## Plans, in order

| Phase | Plan | Items | Size |
|---|---|---|---|
| 0 | [phase-0-spikes.md](phase-0-spikes.md) | The row model's five spikes on ClickHouse 25.8, each gating the decisions it can overturn | Small to medium. Throwaway code, one results file |
| 1 | [phase-1-schema-and-raw-writes.md](phase-1-schema-and-raw-writes.md) | New schema script; bulk writer for spans, logs and the five point tables; 25.8 in the test container | Large. The schema and the whole writer |
| 2 | [phase-2-ingestion-worker.md](phase-2-ingestion-worker.md) | `ClickHouseIngestionWorker`: linger, day split, dedup tokens across retries, out-of-retention drop, drain | Medium. One new worker, registration swap |
| 3 | [phase-3-derived-writes.md](phase-3-derived-writes.md) | `trace_index`, both rollups, `metric_catalog`, `metric_series`, written after the raw insert | Medium. Writer and two in-process trackers |
| 4 | [phase-4-trace-and-rollup-reads.md](phase-4-trace-and-rollup-reads.md) | Trace repository (list anchors, detail, samples, slow count, analytics, export), rollup reads, alerts | Large. The trace read path |
| 5 | [phase-5-log-and-metric-reads.md](phase-5-log-and-metric-reads.md) | Log repository, metric repository, resource repository | Large. Two read paths |
| 6 | [phase-6-retention-harness-docs.md](phase-6-retention-harness-docs.md) | Partition retention plus catalog deletes; test and stress-harness cleanup; documentation | Medium |
| 7 | [phase-7-measurement.md](phase-7-measurement.md) | Stress ramp, write-only ramp and read probes against every target; results recorded | Medium. Runs, not code |

Why this order:
- Phase 0 first because a failed spike changes the row model, and every later phase is built on it.
- Phases 1 to 3 are the write path, bottom up: tables and raw rows, then the worker that batches them, then the
  derived rows. After phase 3 the database holds everything the reads need, so the read phases can be tested against
  data written by the real writer.
- Phase 4 before 5 because trace reads carry the hardest semantics (anchors) and the dashboard and alerts depend on
  the rollups, which phase 4 also covers.
- Phase 6 removes what the old layout left behind once nothing reads it.
- Phase 7 measures the finished whole; earlier phases measure only what they need to (spike-sized checks).

Between phases 1 and 5 the ClickHouse provider is incomplete on the branch: the API host does not work on ClickHouse
until phase 5 is done. That is acceptable under P6 (replace in place; nothing merges until the user decides).

## Decisions that apply to every phase

These come from reading the current code against P1 to P8. **R1, R4, R6 and R9 were confirmed by the user on 2026-10-09**; the rest are unreviewed, say if any is wrong.

| # | Decision |
|---|---|
| R1 | **Derived writes happen inside the bulk writer's flush**, after the raw insert succeeds, not in a separate stage of the worker. The shared test bases seed through `ITelemetryBulkWriter` (P7), so a flush must leave `trace_index`, the rollups and the catalog populated, exactly as the views do today. The worker (P1) adds batching, the day split and token reuse around that call |
| R2 | **Dedup tokens reach the writer through a ClickHouse-only method**, `ClickHouseBulkWriter.FlushTracesAsync(spans, token)` (and the log and metric equivalents), which `ClickHouseIngestionWorker` calls directly. The `ITelemetryBulkWriter` methods (P2, unchanged) mint a fresh token per call, which is what a test seeding once needs |
| R3 | **The worker swap is done by registration, with no Core change.** `AddClickHouseCollectorServices` removes the `TelemetryIngestionWorker` hosted-service descriptor that `AddKeryheTelemetryCollector` added and adds `ClickHouseIngestionWorker`; it throws at startup if `AddKeryheTelemetryCollector` has not run first (the order `Collector.Server` already uses). `RollupWorker` and `MetricTouchWorker` stay registered and idle (`ClickHouseRollupStore.FedByViews` stays `true`, its doc comment updated to "written by the provider"; `ClickHouseMetricTouchStore` stays a no-op) |
| R4 | **Read repositories implement the Core interfaces directly** instead of inheriting `TraceReadRepositoryBase`, `LogReadRepositoryBase` and `MetricReadRepositoryBase`, whose SQL assumes reference-table joins, JSON attribute text, metric surrogate ids and `*_unix_nano` columns. P2 freezes contracts, not Core internals: where a base holds provider-neutral C# the new repositories need (the metric series pipeline's per-stream math, top-N and "other" folding, `EstimateMax`/`EstimateMin`, search parsing, filter validation, export row shaping), that code moves into an `internal`/`public` static helper in Core that both the base and ClickHouse call. The relational providers' behaviour must not change, which their own test runs confirm |
| R5 | **Rollup tables keep the shared column names and meanings** (`bucket_start_unix_nano`, `request_count`, `error_count`, `sum_duration_nanos`, `max_duration_nanos`, `h00`..`h23`, `record_count`, `severity_number` with `-1` for none) and add the row model's `operation` column. That keeps `RollupReadRepositoryBase` (its SQL runs unchanged) and `RollupWriteTestsBase`'s raw SQL working. This replaces the row model's `minute`/`b00`/`duration_sum_ns` names; the grain, engine, sort order and partitioning are as the row model says |
| R6 | **A metric's `Id` is a hash, and a catalog "instance" is a service.** `MetricInfo.Id` and `MetricSeriesQuery.MetricId` are `Int64` hashes of `(tenant, service_name, metric_name, metric_type)`, the `metric_catalog` key, computed the same way in the writer and the reader. The `groupBy=instance` catalog therefore returns one row per (service, metric, type), not one per resource. The UI already uses the id per service (`metric-detail.component.ts` picks the instance whose `serviceName` matches), so it is unaffected; the difference is recorded in the provider README |
| R7 | **Attribute values come back as strings.** Core's attribute models are filled from the `Map(String, String)` columns with string values (row model risk 1). Trace detail, logs and exports show `"500"` where a relational provider shows `500`. Accepted and documented; no Core change |
| R8 | **Ids at the edges.** Trace ids are `UUID` and span ids `UInt64` (Phase 0 spike 1: the driver cannot write raw bytes into `FixedString`). Every id parameter and result goes through one helper set in `ClickHouseIds` (`TraceIdToGuid(hex)` / `GuidToTraceIdHex(guid)`, `SpanIdToUInt64(hex)` / `UInt64ToSpanIdHex(value)`, built over the id bytes); queries bind typed parameters (`{t:UUID}`, `{s:UInt64}`), never per-row SQL conversions. SQL-side `hex(trace_id)` does not give the OTLP hex id, so nothing reads it. A malformed id is a `400` from the existing validation, never a ClickHouse exception |
| R9 | **Timestamps are stored at 100 ns resolution** (Phase 0 spike 1: `DateTime64` through `ClickHouse.Client` carries .NET ticks). `duration_ns` is computed from the original nanoseconds. Confirmed 2026-10-09 (exact `Int64` nanoseconds were the alternative and were declined); see `phase-0-results.md` |

## Out of scope

- The relational providers, their schemas and their write path (P1, P2).
- `plans/collector-improvements/` (P4 orders it after this). Its phase 1 will need a ClickHouse counterpart in
  `ClickHouseIngestionWorker`; that phase's plan gets amended then, not here.
- An hour rollup tier (row model decision 15): added only if phase 7 shows 7-day reads over 3 s.
- Replication, sharding, ClickHouse Keeper.
