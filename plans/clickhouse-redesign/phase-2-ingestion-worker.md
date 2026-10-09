# Phase 2: ingestion worker

Part of the [ClickHouse redesign](README.md). Adds `ClickHouseIngestionWorker` (P1), which drains the shared
`TelemetryIngestionChannel` and writes through `ClickHouseBulkWriter`'s token overloads with the redesign targets'
batching: collector-side linger, one flush loop per signal, the batch split by day partition, and late days buffered
separately (diagnosis results, "Redesign targets": Late data, Batching).

## Decisions

| # | Decision |
|---|---|
| 1 | The shared channel, its record-count gates, `MarkEnqueued`/`TryTakeEnqueued` and the gRPC services are used as they are. The worker is a new `BackgroundService` in `Keryhe.Telemetry.ClickHouse`, registered by the swap in README R3 |
| 2 | One drain loop per signal. Each loop sorts drained records into **day buffers** keyed by the record's partition day (UTC date of `start_time`, `timestamp` or `time`) |
| 3 | The current day's buffer flushes when it reaches `MaxBatchRecords` or has lingered `LingerMilliseconds` since its first record. An older day's buffer flushes when it reaches `MaxBatchRecords` or has lingered `LateLingerMilliseconds`. A buffer for a day after today (clock skew) is treated as current |
| 4 | Each flush is one insert per day per table with a token minted when the buffer is sealed and reused on every retry of it (row model decision 6). Phase 0 spike 3 showed a multi-partition insert is fully deduplicated, so this is a choice, not a need: a day gets its own insert for its own linger and so that a large late day does not hold back the current day. A buffer is never re-batched after sealing; if it must be split (an oversize buffer), each piece gets a new token. `BatchSize` on the bulk copy is at least the piece's row count (spike 3: one token across several INSERT statements drops all but the first) |
| 5 | A record older than the longest retention window for its signal (read from the control plane's retention settings, refreshed every 5 minutes) is dropped before buffering and counted on `records_dropped` with tag `reason=out_of_retention` |
| 6 | Retry: the shared worker's bounded exponential backoff with full jitter and `MaxFlushRetries`, reusing the token. After the last retry the batch is dropped and counted (`reason=retries_exhausted`) |
| 7 | The gate is released for a record when the flush that carried it finishes, success or drop, as today, so backpressure covers records waiting in day buffers |
| 8 | Instruments: the existing `IngestionMetrics` set with the same names and tags (`flush_duration`, `flush_retries`, `records_flushed`, `flush_batch_size`, `commit_lag`, `records_dropped`), so the stress harness reads ClickHouse unchanged; plus `late_buffer_records` (observable gauge, records waiting in non-current day buffers) |
| 9 | Shutdown drains as the shared worker does: complete the writers, flush every buffer regardless of linger, until empty or `ShutdownTimeout`; what remains is dropped and counted (`reason=shutdown`) |
| 10 | Options in their own section, `Telemetry:ClickHouse:Ingestion`: `LingerMilliseconds` (2,500), `LateLingerMilliseconds` (30,000), `MaxBatchRecords` per signal (300,000 spans, 300,000 logs, 300,000 points; Phase 0 spike 2 may change these), `MaxLateDayBuffers` (8; past it, the oldest buffer flushes early). Validated at startup. The shared `Telemetry:Ingestion` section's gate sizes still apply; its `FlushConcurrency` and batch sizes do not on ClickHouse, which the docs say |

## Steps

1. **Options and validation**: `ClickHouseIngestionOptions`, bound in `AddClickHouseCollectorServices`.
2. **Registration swap (README R3)**, with a startup exception naming `AddKeryheTelemetryCollector` when its worker
   descriptor is not found.
3. **Day buffers**: a small class per signal holding records, their gate counts, the export lists for `commit_lag`,
   first-record time and the sealed token.
4. **Drain loop**: read from the channel with a timeout equal to the nearest buffer's remaining linger; route records;
   seal and flush due buffers (current day first, then late days oldest first); release the gate.
5. **Retention filter (decision 5)**: a singleton that caches the windows from `IRetentionSettingsRepository`. The
   collector does not register that repository today (it is API-side, `Add<Provider>ControlPlaneApiServices`); add a
   read-only lookup to the collector's control-plane registration on each relational provider, or read the row with a
   small dedicated query in the ClickHouse project through `ConnectionStrings:ControlPlane`. Prefer the second: it
   keeps the relational providers untouched (P1).
6. **Shutdown** as decision 9.
7. **Remove** what ClickHouse no longer uses from the old path: nothing in Core changes; the ClickHouse provider simply
   stops relying on `TelemetryIngestionWorker`.

## Tests

- **Unit (no Docker)** with a fake writer behind a seam (an internal interface over the three token overloads): records
  of three days in one drain go to three inserts with three tokens; a retry reuses its token; the current day flushes on
  linger, a late day on late linger; `MaxLateDayBuffers` forces the oldest out; out-of-retention records are dropped and
  counted; the gate is fully released after success, drop and shutdown; `commit_lag` is recorded once per export.
- **Testcontainers (ClickHouse)**: end to end through the real gRPC services (`CollectorAuth`-style `TestServer` host with
  ClickHouse behind it): an export with yesterday's and today's spans lands in two partitions; a forced first-attempt
  failure (a fault-injecting decorator around the writer) followed by a successful retry stores each row once.
- Run `ramp-write-only` on ClickHouse once (smoke profile) to confirm the harness reads the instruments.

## Documentation

- `docs/CONFIGURATION.md`: `Telemetry:ClickHouse:Ingestion`, and which `Telemetry:Ingestion` keys do not apply on
  ClickHouse.
- CLAUDE.md waits for phase 6.

## Done when

- The collector on ClickHouse runs `ClickHouseIngestionWorker` and not `TelemetryIngestionWorker`.
- Late and current data land in their own day partitions in large inserts, and a retried batch is stored once.
- Every shared write-path instrument is reported, plus `late_buffer_records`.
