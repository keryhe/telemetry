# Phase 4: writers, lease and compaction on a shared remote root

Part of [Parquet on object storage](README.md). Backend-neutral changes that only matter once several processes on several
machines share one root.

## Decisions

| # | Decision |
|---|---|
| 1 | **Writer id on object storage** (O8): `WriterId` if configured, used without a lock; otherwise `<machine>-<8 random hex>` per process start. Local keeps today's lock-file identity |
| 2 | **Heartbeat**: each collector `PUT`s `_writers/<id>` with `{lastSeenUnixMs, host, started}` every `Storage:HeartbeatSeconds` (60). On object storage this is liveness only, not a lock |
| 3 | **Duplicate-id detection**: a manifest write uses `If-Match` on the ETag this writer last wrote to it (a first write: `If-None-Match`). A 412 means another process wrote this writer's manifest: the flush fails permanently, the collector logs an error naming the id and reports not ready (`CollectorReadinessCheck`). Two collectors with one configured id are caught at their first shared partition, not silently interleaved |
| 4 | **Sequence invariant** (README "Correctness"): `ManifestWriter` takes `max(manifest.NextSequence, through + 1)` where `through` is the newest generation's (hour or day) value for this writer. It already reads `through` on every commit (for pruning), so it moves before the name is chosen; no extra request |
| 5 | **Manifest retirement**: the compactor deletes a writer manifest when (a) the writer's heartbeat is older than `Compaction:WriterDeadMinutes` (60) or absent, (b) every file it lists has a sequence at or below the retained generation's `through`, and (c) that generation is past `GraceSeconds`. For a configured id that later returns, decision 4 makes it safe. Heartbeat objects of retired writers with no manifest left anywhere in the scanned days are deleted after `WriterDeadMinutes` × 24 |
| 6 | **Lease is CAS** (O9): read the lease and its ETag; take it if expired or ours with `PutIfMatch` (or `PutIfAbsent` when there is none). A lost CAS means another instance took it. Generation publishing stays exclusive, so a brief overlap stays harmless, as today |
| 7 | **Compactor I/O**: inputs are read by `ReadUriOf` (remote reads in DuckDB); outputs are `COPY`ed to the work area and committed with `PutFileAsync`. `Describe` reads the local output before upload, as now |
| 8 | **Orphan sweep by listing**: unchanged rule (in no manifest, older than at least an hour), using the listing's `LastModified`. No remote temp files exist to sweep (O11); the work area is cleaned on startup |
| 9 | **Retention** uses `DeletePrefixAsync` per tenant day; the row count from the manifests first, as now. A partially failed prefix delete is retried on the next sweep (a day folder that is half gone is still expired, and readers never reach back past the window) |

## Tests

- Unit (local store): sequence invariant with the manifest deleted after a compaction; retirement rules (alive writer kept, dead
  writer with unfolded files kept, dead folded writer retired); CAS lease handover and expiry with a controllable `TimeProvider`.
- MinIO and Azurite: two collector processes with distinct ids and one root, two compactors racing, readers resolving throughout:
  no lost, duplicated or missing rows against a ledger (the first plan's spike 3 harness, now in the suite as an opt-in
  `Suite=ParquetContention`); two collectors with one configured id fail as decision 3 says.

## Done when

The contention suite passes on both emulators and the decisions' unit tests pass.
