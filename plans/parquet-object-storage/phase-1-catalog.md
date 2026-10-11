# Phase 1: the catalog

Part of [Parquet on object storage](README.md). Replaces the manifest protocol with the relational catalog (C1-C18, C24) on **today's
local file store**, before any storage abstraction or cloud code exists, so every existing Parquet suite tests the change and a
regression is attributable to it. The bucket-side work (phase 2 onwards) then has no manifests to carry.

## What goes

`ManifestWriter`, `ParquetManifest`/`ParquetManifestFile`, `ParquetManifests` and `ParquetPartitionState` (generations, `through`,
`HourThrough`, sequences, the read-order rule), `ParquetWriterIdentity` (lock files, `_writers/`), `ParquetSchemaFile`
(`_schema.json`), the object-file lease in `ParquetCompactionLease`, `Telemetry:Parquet:WriterId`, the manifest methods of
`IParquetFileStore` (`ListManifestNamesAsync`, `ReadManifestAsync`, `WriteManifestAsync`, `TryPublishManifestAsync`,
`ReadTextAsync`/`WriteTextAsync`, `EnumeratePartitions`), and the request-rollup, log-rollup and metric-catalog **files** (C18; they
remain as `_day/` files written by the inlined move, and as files when `InlineDerived=false`).

## Decisions

| # | Decision |
|---|---|
| 1 | **Schema component** (C4): `schema/PostgreSQL-ParquetCatalog.sql`, `SqlServer-ParquetCatalog.sql`, `MySQL-ParquetCatalog.sql` with the README's tables and indexes (partial/filtered on live rows where the provider has them, `state`-leading on MySQL; clustered on the read path on SQL Server and MySQL; low autovacuum scale factors on PostgreSQL's `parquet_files`), `parquet_catalog_schema_version` at 4.0.0; `apply-schema.sh catalog <postgresql|sqlserver|mysql> [database]` with `CATALOG_TARGET_VERSION="4.0.0"` (ClickHouse and Parquet refused for this component). Applying it to the control-plane database or another one are both fine: no foreign key crosses into the control plane |
| 2 | **`IParquetCatalog` in Core, implementations in the provider packages** (README "Packages and registration"): `PostgreSqlParquetCatalog`, `SqlServerParquetCatalog`, `MySqlParquetCatalog` (Dapper, on `ControlPlaneRepositoryBase`'s pattern), registered by `Add<Provider>ParquetCatalogServices(configuration)` from `ParquetCatalogConnection.FromConfiguration` (C3). PostgreSQL uses its own keyed `NpgsqlDataSource` (`"ParquetCatalog"`). SQL Server's statements run at `DEADLOCK_PRIORITY LOW` with one retry on 1205, as its other writers do. Idempotent inserts are `ON CONFLICT DO NOTHING` (PostgreSQL), `WHERE NOT EXISTS ... WITH (UPDLOCK, HOLDLOCK)` (SQL Server) and `ON DUPLICATE KEY UPDATE id = id` (MySQL; **never `INSERT IGNORE`**, which also swallows truncation and conversion errors). The lease's first insert treats a duplicate key as "another holder won" (R23). An `InMemoryParquetCatalog` in `Keryhe.Telemetry.Parquet.Tests` serves the no-Docker suite |
| 3 | **Root marker** (C5, C7, C11, C17): the collector's startup calls `EnsureRootAsync(RootId, version, tables hash, kind, location)` and the API host's `VerifyRootAsync`; a mismatch fails startup naming what differs. A location already held by another root, or nested inside or around one, fails startup naming that root. A root folder holding `_schema.json` or any `_manifest.*` file with no root row is a manifest-era root: refused with "this root was written by the manifest-based Parquet provider (2.0.0); start a new root" (C7). `schema/apply-schema.sh telemetry parquet <root>` still only creates the directory |
| 4 | **Object keys and file metadata**: writer files `part-<writer id>-<token>.parquet` (no sequence), compactor outputs `c-<uuid>.parquet`, in today's folders (C9). The token is the flush's deduplication token, so a retried flush produces the same keys. Every file's `COPY` writes Parquet key-value metadata: root, table, tenant, day, hour, level, token, writer, min/max time, rows (C24) |
| 5 | **A flush is stage, upload, commit** (C13, C14, C15). `ParquetBulkWriter` writes every file of a flush (the raw table's partitions, then `trace_index` or `metric_series`) to temp files and builds the inlined rows (rollups, metric catalog; C18) in memory, then: on a retry, `CommittedAsync(token)` ends the flush if the token committed; `StageAsync(token, files)`; moves the files into place (local: rename; from phase 2 `PutFileAsync`, concurrently, bounded by `MaxConcurrentRequests`); then one `CommitAsync(token, files, inlined rows)`. `Lost` (a staged row was abandoned meanwhile) retries the flush. `WriteDerivedAsync`'s separate retry loop goes. **Failures by kind**: an *unavailable* failure (decision 7) holds; a *transient* failure anywhere (I/O, out of memory, a store or catalog error that is neither unavailable nor permanent) fails the flush, which the worker retries under the same token. A *permanent* failure (by `ParquetFlushErrorClassifier`) in the **raw** table's write fails the flush and is bisected as today. A *permanent* failure in a **derived** table's write or rows must not cost raw records: the bulk writer catches it per derived table, leaves that table's files or rows out of the flush, records `derived_rows_dropped{table, reason=permanent}` with a log naming the table and the error, and commits the rest in the one transaction. The exception never reaches `BatchBisector`. A retry of the same token re-derives the table and drops it the same way, since the input is the same |
| 6 | **Unknown commit outcomes** (R1, C14): decided by `CommittedAsync(token)`, for flushes and swaps alike. `PruneCommitsAsync` runs each compaction pass. The old rule that the retry schedule must fit inside `GraceSeconds`, and `RegisteredKeysAsync`/`ExistsAsync`, are not built |
| 7 | **The unavailable outcome** (C6). Core gains `FlushErrorKind.Unavailable`; `PartitionedIngestionWorker` retries it with the same capped, jittered backoff but without counting it against `MaxFlushRetries`, logging once per minute while it lasts and recording `flush_unavailable_seconds{signal}`. `ParquetFlushErrorClassifier` consults a catalog classifier from the provider (connection, timeout, failover and deadlock errors unavailable; a missing table or permission error permanent, so a misconfigured catalog refuses rather than holds forever) and, from phase 2, the store's classifier. Other providers' classifiers never return it, so ClickHouse and the shared worker are unchanged. `CollectorReadinessCheck` reports not ready while any signal holds. Shutdown drains what it can within `ShutdownTimeout` and counts the rest on `records_dropped{reason=shutdown}` |
| 8 | **The compactor finds its work in SQL** (P1). `CompactionCandidatesAsync(root, sinceDay, now)` returns, per (table, tenant, day, hour), the live files' count by level, oldest `committed_at`, rows and whether the hour or day is closed (`CloseHourGraceMinutes`, `CloseDayGraceHours`, compared in database time), plus the tenant-days whose inlined rows are due to move: a pass reads only those partitions' live files and never lists the bucket. The scan stays bounded by `ScanDays`, with the periodic full scan (`FullScanMinutes`) as a query over all days, not a walk |
| 9 | **A compaction is a swap** ("Correctness" 2). Minor (only when `MinorFileThreshold` or `MinorAgeSeconds` triggers; off by default on object stores, C20): an hour's level-0 files fold into one level-1 file. Major: a closed hour's live files rewrite into one level-1 set. Day: a closed day's live files rewrite into level-2 files under `_day/`. Each stages its outputs, writes them to the work area, moves them into place, then `SwapAsync(compaction id, inputs, outputs)`. `Lost` (an input was no longer live): its outputs' staged rows and objects are deleted at once. An unknown outcome: `CommittedAsync(compaction id)` decides (R1). `ParquetCompactor`'s DuckDB side (`WriteCompactedAsync`, the sort and split rules, `Describe`) is unchanged |
| 10 | **Deletion and clean-up by the catalog**: each pass takes `DueDeletionsAsync(GraceSeconds)` (superseded files and moved inlined rows past the grace, by database time), deletes the objects, then `ForgetAsync`s the rows; a failed object delete keeps its row for the next pass. `AbandonStagedAsync(StagedTimeoutMinutes)` deletes abandoned staged rows, then their objects (C15). The **safety-net sweep** every `Compaction:BucketSweepDays` (7; 0 = off) lists the folders of the days that have catalog rows, reads the catalog after listing, and deletes objects no row names that are older than 24 h (plus dead `.tmp` files as today). It is the only bucket listing in the system |
| 11 | **Minor day merge** (P9). A late level-0 file in an hour of a merged day folds, with any others, into **one** new level-2 file; the day's existing level-2 files stay. The whole day is rewritten only when its level-2 add-on files pass `DayAddOnMaxFiles` (8) or `DayAddOnMaxRows` (5,000,000). Late inlined rows for a moved day are moved as one add-on file under the same limits |
| 12 | **Per-backend defaults** (C19, C20): object stores `LingerMilliseconds` 120,000, `LateLingerMilliseconds` 300,000, `MinorFileThreshold` 60, `MinorAgeSeconds` 0; Local 30,000, 120,000, 100, 600; a configured value wins. Take effect once phase 2 has a `Kind`; phase 0 spike 4 and phase 6 runs 3b and 10 confirm the numbers. The longer linger raises resident records per gate: the startup warning that each gate must hold at least `MaxBufferRecords` is unchanged, and the provider README states the memory per collector at the default buffer size |
| 13 | **Retention by the catalog's keys** (C16). `ParquetRetentionSweeper` asks `ExpiredDaysAsync` for the tenant-days before the shortest window's cutoff, keeps those before their own tenant's cutoff, then per tenant-day: the raw rows from `SUM(row_count)` (P11), `ExpireDayAsync` (readers stop seeing it at once; it returns every expired key of the day and deletes the day's inlined rows), `DeleteManyAsync` of those keys, `ForgetAsync`. A half-done day stays expired and is finished by the next sweep; a file committed into the day later is expired by the next sweep. Tenants come from `parquet_files` and the inlined tables, so orphan tenants expire on the built-in window |
| 14 | **Lease** (C5): `ParquetCompactionLease` becomes `TryAcquireLeaseAsync(owner, LeaseSeconds)` / `ReleaseLeaseAsync` on the catalog. Two holders are impossible while the database is consistent; the swap (decision 9) still makes an overlap harmless. Startup requires `StagedTimeoutMinutes` x 60 > `LeaseSeconds` |
| 15 | **Writer registry** (C12): each process start registers `<host>-<8 hex>` with its role and heartbeats every `Catalog:HeartbeatSeconds` (30); the compactor deletes rows not seen for `Catalog:WriterRowRetentionDays` (7). Collectors write their id into their files' keys and rows. A configured `Telemetry:Parquet:WriterId` logs a warning that it is ignored |
| 16 | **Reads** (C10): `ParquetFileResolver` calls `LiveFilesAsync` per tenant and day range for every table at once, caches each day's snapshot for `Catalog:CacheSeconds` (5) and filters by table and `[min_time, max_time]` overlap in memory. The compactor and retention read uncached. Startup requires `GraceSeconds` ≥ `CacheSeconds` + 60 |
| 17 | **Inlined derived tables** (C18). Write: the flush's commit inserts its rollup and metric-catalog rows. Move: the compactor writes a closed tenant-day's unmoved rows (folded: the rollup rows summed per minute, the catalog rows merged per metric) into one `_day/` file per table and swaps (`moved_to_file_id`). Read: `ParquetRollupReadRepository` runs the shared `RollupReadRepositoryBase` SQL twice, on DuckDB over the moved day files and on the catalog connection over the unmoved rows (excluding rows whose file is in the snapshot), and hands both row sets to the existing fold; the base gains the table-name and `root_id` hooks this needs, and the relational providers' overrides are unchanged. The per-operation figures, `ParquetMetricReadRepository`'s catalog read and `ParquetResourceReadRepository`'s service list do the same (the service list becomes three reads, unioned in memory). Retention deletes a day's inlined rows with the day (decision 13). `InlineDerived=false` keeps today's files per table |
| 18 | **The stress observer counts from the catalog** (P10): files and rows per table and tenant from `parquet_files`, inlined rows from their tables; the end-of-run correctness count stays DuckDB over the live files the catalog lists, which is what a reader reads |

## Steps

1. Schema scripts and `apply-schema.sh` (decision 1); `ColumnLimitsTests`-style check that the three scripts declare the same
   columns and sizes, and that the inlined tables' columns match `ParquetTables`' definitions.
2. `IParquetCatalog` and its models in Core; `FlushErrorKind.Unavailable` and the worker's hold (decision 7); the three
   implementations and their `Add...ParquetCatalogServices`; the in-memory fake.
3. The bulk writer's stage/upload/commit (decisions 4-7), the resolver (16), the inlined tables' write, move and reads (17), the
   compactor (8-11, 14), retention (13), root marker (3), writer registry (15), on the existing local store with its manifest
   methods removed.
4. Both hosts' catalog switch (README "Packages and registration"), `ConnectionStrings:Catalog` handling and the startup
   validator's catalog checks (registration, grace against cache, staged timeout against lease).
5. Test infrastructure: `ParquetProviderContainer` applies `PostgreSQL-ParquetCatalog.sql` to the control-plane PostgreSQL it
   already composes and passes `RootId` per run; `ParquetObserver` (decision 18).

## Tests

- **`ParquetCatalogContractTestsBase`**: every `IParquetCatalog` operation, run against the in-memory fake (no Docker) and against
  PostgreSQL, SQL Server and MySQL in `IntegrationTests` (`--filter Provider=...`, as `ControlPlaneTestsBase`): a duplicate token
  commits once; a commit that lost a staged row to `AbandonStagedAsync` rolls back as `Lost`; abandon deletes rows that are still
  staged only (a row a commit turned live in between is kept); a swap that loses to a concurrent swap; a swap that loses to
  retention; the lease under contention with database time, including two first inserts racing; due deletions only after the
  grace; expire returns every key of the day and a file committed afterwards is expired by the next call; two roots in one
  database never seeing each other's rows; a second root on the same or a nested location is refused; the inlined move stamps
  exactly the rows it wrote. Each provider's plan check for `LiveFilesAsync`, `CompactionCandidatesAsync` and the inlined
  rollup read (index, not scan), negative-controlled as the PostgreSQL and SQL Server plan tests are.
- `Keryhe.Telemetry.Parquet.Tests` and `--filter Provider=Parquet` (and `ParquetCompacted`) pass with the same assertions on the
  fake and on PostgreSQL respectively: their manifest-specific tests (`CompactionTests`' generations and `through`,
  `PartitionTests`' manifest order, `StartupTests`' lock files) are rewritten against the catalog's equivalents, the rest change
  only in construction. The rollup, operation, metrics-catalog and service-list tests run with `InlineDerived` on and off, and
  across a day move (the same answers before and after, with a reader holding a pre-move snapshot).
- New: a crash between upload and commit leaves a staged row that clean-up removes with its object; a retried flush whose first
  commit succeeded (acknowledgement dropped) stores each row once; a transient derived failure retries the whole flush and leaves
  no raw rows visible without their derived data; a permanent derived failure (a fault-injecting file writer that refuses one
  derived table, and one that refuses the inlined rows) commits the raw rows and the other derived tables, stores each raw record
  once, drops none, counts the derived table's rows on `derived_rows_dropped{reason=permanent}` and is never bisected; a permanent
  failure in the raw table's write is still bisected; a compactor whose commit outcome is unknown keeps its outputs when the token
  committed and deletes them when not; a late file in a merged day adds one level-2 file, and the limits trigger a full day
  rewrite; late inlined rows for a moved day become one add-on file; a manifest-era root is refused; the grace and staged-timeout
  validations; **a catalog outage longer than the whole `MaxFlushRetries` schedule** (a fake catalog that throws an unavailable
  error for a controllable time) drops nothing, makes the gate refuse exports and readiness fail, and ingestion resumes with every
  record stored once.

## Done when

The suites pass on every catalog provider, and a local `ramp-write-only` and `smoke` stress run on the same machine as phase 7 of
the first plan are recorded beside its numbers, with the catalog's statement rate and latency and the inlined tables' row counts
(O3: the catalog is now on the write path; the difference is reported, not required to be within noise).
