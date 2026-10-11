# Phase 2: ingestion worker

Part of the [Parquet + DuckDB provider](README.md). The Parquet provider batches for files: few, large writes, the
same need `ClickHouseIngestionWorker` meets for ClickHouse with day buffers, linger, tokens reused across retries,
out-of-retention drops, tenant tallies, bisection of a refused batch and a draining shutdown. This phase makes that
worker shared instead of copying its 430 lines.

## Decisions

| # | Decision |
|---|---|
| 1 | **Extract the worker into Core** as `PartitionedIngestionWorker` (`Core/Data`), parameterized by a partition-key function (UTC day for ClickHouse, UTC hour for Parquet), an `IPartitionedFlushWriter` (the token overloads: today's `IClickHouseTokenWriter` generalized), the linger options and an `IRetentionWindows`. `ClickHouseIngestionWorker` becomes a thin subclass or registration of it with ClickHouse's options and key. **No behaviour change for ClickHouse**: its existing `ClickHouseIngestionWorkerTests`, `ClickHouseIngestionWorkerDbTests` and `ClickHouseParallelFlushTests` must pass unchanged, which is this phase's guard. If the extraction turns out to need ClickHouse-visible behaviour changes, stop and copy instead (the fallback, recorded in the implementation notes) |
| 2 | `IRetentionWindows` and `RetentionWindowCache` move from the ClickHouse project to Core (they already read the control plane by driver type name, with no ClickHouse dependency) |
| 3 | **Buffers are keyed by UTC hour** across tenants. A flush writes one file per (tenant, hour) through `ParquetBulkWriter`'s token overload (phase 1 decision 9). The open hour lingers `LingerMilliseconds` (30 s), earlier hours `LateLingerMilliseconds` (120 s); past `MaxLateBuffers` (24) the oldest flushes early |
| 4 | **Seal on size** at `MaxBufferRecords` or `MaxBufferBytes` (the export bytes the gate already tracks), whichever first; an oversize buffer is cut into pieces with new tokens, as ClickHouse does |
| 5 | **Gate wait**: like ClickHouse, the default `Telemetry:Ingestion:MaxGateWaitMilliseconds` is the linger plus 2 s, since room is freed a buffer at a time. With a 30 s linger the gate sizes in `Telemetry:Ingestion` must hold at least 30 s of traffic; the provider README gives the sizing rule and the startup validator warns when `MaxQueued*PerSignal` looks smaller than one linger at the configured `MaxBufferRecords` |
| 6 | **Flush errors**: `ParquetFlushErrorClassifier` treats disk full, permission denied, a read-only filesystem and a missing root as transient (they are operator-fixable, and dropping on them would lose data a retry could save) and DuckDB conversion errors on a record as permanent (so `BatchBisector` isolates the record) |
| 7 | **Registration** swaps out the shared `TelemetryIngestionWorker` exactly as `AddClickHouseCollectorServices` does (ClickHouse README R3), with the same startup exception when `AddKeryheTelemetryCollector` has not run. `RollupWorker` stays registered and idle (`ParquetRollupStore.FedByViews` true; the writer writes the rollups in phase 3) and `MetricTouchWorker` drains to a no-op `IMetricTouchStore` |
| 8 | Instruments are the shared `IngestionMetrics` set with the same names; `late_buffer_records` keeps its name. `commit_lag` is enqueue to manifest commit, which is when a record becomes readable |

## Steps

1. Move `IRetentionWindows`/`RetentionWindowCache` to Core; ClickHouse references them.
2. Extract `PartitionedIngestionWorker` and its options base from `ClickHouseIngestionWorker`; switch ClickHouse to it;
   run the ClickHouse suites (`--filter Provider=ClickHouse` and the `ClickHouseIngestion` folder).
3. `ParquetIngestionOptions` (`Telemetry:Parquet:Ingestion` or inside `Telemetry:Parquet`, decided with the README's
   configuration table), registration, `ParquetFlushErrorClassifier`.
4. Shutdown drain verified against the file store: every buffer committed before the host stops.

## Tests

- The ClickHouse worker suites, unchanged (decision 1).
- Parquet worker tests with a fake clock and the real file store in a temp directory: linger and size seals; an hour
  boundary splits buffers; late hours linger longer; a retried flush (fault-injected writer) stores each file once; a
  permanent error bisects to the bad record; out-of-retention records are dropped and counted; shutdown commits every
  buffer; gate and tenant tallies return to zero.

## Done when

The collector host runs with `Database:Provider=Parquet` and writes span files from the test data generator's live
traffic, and both providers' worker tests pass.

## Implementation note (2026-10-10, uncommitted)

Done: `Core/Data/PartitionedIngestionWorker` (the ClickHouse worker, extracted; buffers keyed by a provider-supplied bucket
number, days for ClickHouse and hours for Parquet), `IPartitionedTokenWriter`, `IPartitionedIngestionOptions`,
`RetentionWindowCache`/`IRetentionWindows` moved to Core (the cache now takes its refresh seconds as a constructor
argument). `ClickHouseIngestionWorker` is a thin subclass with the same constructor, so its tests compile and pass
unchanged (`ClickHouseIngestionWorkerTests` 15, and the Docker-backed ClickHouse worker and parallel-flush tests 4). No
fallback copy was needed. Parquet: `ParquetIngestionWorker`, `ParquetFlushErrorClassifier`, no-op `ParquetRollupStore` and
`ParquetMetricTouchStore`, registration swapping the shared worker, and `Collector.Server` handles `Database:Provider=Parquet`
(references the Parquet project). 11 new tests (33 in the Parquet suite).

Checked live: the collector on a scratch root with the test data generator wrote span files for three tenants and nine
services (41 span files in a minute at a 3 s linger), no leftover temp files, clean shutdown. Logs and metrics are dropped and
counted (`permanent`) until phase 5, as expected: `ParquetBulkWriter`'s log/metric flushes throw `NotSupportedException`,
which the classifier calls permanent.

Differences from the plan:
- **Settings** live in `Telemetry:Parquet` itself (`LingerMilliseconds`, `MaxBufferRecords`, new `MaxLateBuffers` 24 and
  `RetentionRefreshSeconds` 300), not a nested `Ingestion` section.
- **No size seal on bytes.** `MaxBufferBytes` is accepted but unused: the gate's `MaxQueuedBytesPerSignal` already bounds
  resident bytes. Sealing is on records only; an oversize buffer is cut into pieces with new tokens (as ClickHouse).
- **Gate-size check** is a startup warning in `ParquetCollectorStartup` (queue smaller than one buffer), not a validator.
  The shipped defaults (200,000 queued, 500,000 buffer) trigger it, so a real deployment should raise `Telemetry:Ingestion:MaxQueued*`
  (or lower `MaxBufferRecords`): the README sizing section is still to write (phase 6).
- Tests use the system clock with short lingers (the worker's linger uses a stopwatch, as in ClickHouse's tests), not a fake clock.
- `ConnectionAgeTests.A_steady_client_is_moved...` failed once in a full CollectorAuth run and passed on rerun (timing-sensitive
  real-Kestrel test; unrelated to this change).
