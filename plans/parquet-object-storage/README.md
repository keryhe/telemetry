# Parquet provider on object storage (S3, Azure Blob, local), with a relational catalog

Written 2026-10-10, **rewritten 2026-10-11 around a catalog database**, and **revised the same day after a third review
(correctness and cost; decisions C14-C25)**. Follows the [Parquet + DuckDB provider](../parquet-duckdb/README.md),
whose U4 deferred object storage to "a later phase behind a storage abstraction introduced now" (`IParquetFileStore`). This plan
makes the Parquet provider run on **AWS S3**, **Azure Blob Storage** and **local disk**, chosen by configuration. Each backend is
its own package (`Keryhe.Telemetry.Parquet.Local`, `.AwsS3`, `.AzureBlob`; O22).

The point of it: collectors and API hosts on **different machines** sharing one root, which v1 rules out (a network share gave
stale manifests), **at a cloud bill dominated by as few storage requests as acceptable performance allows** (C23).

**What the rewrite changed.** The first version kept v1's file manifests and made them safe on object storage with conditional
writes. Two reviews (R1-R15, P1-P12 below) showed that most of its complexity, and most of its request cost, came from keeping
the list of files in the bucket. Every comparable system moved that list into a catalog (Iceberg's catalog, Tempo's tenant
index, DuckLake's SQL catalog, InfluxDB 3's PostgreSQL catalog, Quickwit's metastore). This plan now keeps it in a **relational
catalog database**: PostgreSQL, SQL Server or MySQL, by default the control-plane database, behind its own connection string. The
bucket holds only data files. That replaces the manifests, the generations and their `through` bookkeeping, writer sequences,
writer-id conflicts, manifest retirement, the listing-based read path and the object-ETag lease, on **all three backends** (C2), so
there is one metadata protocol.

**What the third review changed.** On object storage the bill is almost all requests, and the plan as written produced about
twelve small objects per tenant per collector every 30 seconds (about 1,440 PUTs per tenant-hour). The revision cuts that by
roughly an order of magnitude without a write-ahead log: a longer linger on object stores (C19), the small derived tables kept as
rows in the catalog until their day closes (C18, DuckLake's "data inlining"), hour merges only (C20) and tenant affinity at the
load balancer (C21); staged catalog rows remove the hourly bucket listing (C15); a local read-through cache keeps repeated reads
off the store (C22). It also closes correctness gaps: a catalog outage dropped batches after ~25 s despite C6 (now held, C6),
the retry-inside-grace timing rule (replaced by commit tokens, C14), retention's prefix delete removing a live file (C16), two
roots on one location deleting each other's files (C17), and per-table caches breaking C13's "together or not at all" (C10).

## Status

Planned 2026-10-10; rewritten 2026-10-11 (decisions C1-C13); revised 2026-10-11 (C14-C25, third review). Not started.

## Decisions

### Catalog decisions (2026-10-11)

| # | Decision |
|---|---|
| C1 | **Our own catalog tables**, not DuckLake: the layout, the row model, ingestion buffering and the compactor stay ours; only where the file list lives changes |
| C2 | **The catalog is used on all three backends**, Local included. The file manifests, `_compacted.*` generations and `ParquetWriterIdentity`'s lock files are removed. A Local deployment now needs the catalog database on its write path (it already needs the control plane for API keys) |
| C3 | **Its own connection string and provider key**: `ConnectionStrings:Catalog` (defaults to `ConnectionStrings:ControlPlane` when unset) and `Telemetry:Parquet:Catalog:Provider` (`PostgreSQL`, `SqlServer`, `MySql`; defaults to `ControlPlane:Provider` when unset), so a busy deployment can move catalog load off the control-plane database |
| C4 | **A third schema component**, `parquet-catalog`, on all three relational providers: `schema/<Provider>-ParquetCatalog.sql`, version table `parquet_catalog_schema_version`, `CATALOG_TARGET_VERSION` in `apply-schema.sh` (`apply-schema.sh catalog <provider>`). It starts at **4.0.0** and bumps only its own third digit afterwards |
| C5 | **Also in the catalog**: the compaction **lease** (one row, taken with one conditional `UPDATE`), the **writer registry**, the **root marker** (what `_schema.json` held), the **commit tokens** (C14) and the **inlined derived rows** (C18). The bucket holds data files only |
| C6 | **Catalog or store outage: hold and retry** (rev. 3). An *unavailable* failure (the catalog's connection, timeout and failover errors; the store's throttling, 5xx and connection errors) is a new flush outcome, `FlushErrorKind.Unavailable`: `PartitionedIngestionWorker` retries it with capped backoff **without** counting it against `MaxFlushRetries`, so buffers stay resident, the ingestion gate fills and exports are refused with `RetryInfo` (the existing backpressure); `CollectorReadinessCheck` reports not ready. Memory stays bounded by the gates. Only shutdown ends a hold: what `ShutdownTimeout` cannot drain is counted on `records_dropped{reason=shutdown}`. (The first version said this and the worker dropped the batch after ~25 s; third review R16) |
| C7 | **Fresh install only.** A root written by the manifest-based provider (2.0.0) is refused at startup with a message; the operator starts a new root. No import tool |
| C8 | **The catalog models files, not manifests**: one row per data file (tenant, table, hour or day, time bounds, rows, bytes, state: **staged**, live, superseded, expired). A commit turns staged rows live; a compaction is one transaction that turns its outputs live and marks its inputs superseded; a reader selects live files. Generations, `through`/`HourThrough`, sequences and per-writer manifests are gone |
| C9 | **The bucket keeps its prefixes**: `<table>/tenant_id=<t>/date=<d>/hour=<h>/` and `.../date=<d>/_day/`. A person can still read the bucket, and moving a root between backends is a copy of the objects plus an update of the catalog's root row. (A tenant-first layout was considered in the third review and not adopted: retention now deletes by key, C16, S3 partitions prefixes as load grows, and per-tenant lifecycle rules are not used since retention is the sweeper, O13) |
| C10 | **Reads use a short cache, one snapshot per tenant-day** (rev. 3): a host asks the catalog for the live files of **every table** of a tenant-day in one statement and caches that list for `Telemetry:Parquet:Catalog:CacheSeconds` (default **5 s**). Every table a request reads for that day comes from the same snapshot, so a flush's raw and derived data are seen together (third review R21) |
| C11 | **One catalog database can serve several roots**: every catalog row carries a `root_id`, the configured name of the root (`Telemetry:Parquet:RootId`), so tests, stress runs or deployments can share one catalog database |
| C12 | **The writer registry is liveness only.** Each process start registers a row with a random id (`<host>-<8 hex>`), its host, role, start and last heartbeat (database time); its files carry that id. No uniqueness is enforced and nothing refuses to start. `Telemetry:Parquet:WriterId` is removed |
| C13 | **A whole flush becomes visible in one transaction**: a flush's raw files, its derived files (`trace_index`, `metric_series`) and its inlined derived rows (C18: the rollups, `metric_catalog`) are uploaded or staged, then made live in one catalog transaction. Readers see a batch's raw and derived data together or not at all. A **transient** or **unavailable** failure anywhere retries the whole flush instead of under-counting. A **permanent** failure in a derived table's write never costs raw records: that table's files or rows are left out, the rest of the flush commits, and the left-out rows are counted on `derived_rows_dropped{table, reason=permanent}`, the accepted under-count of the row model; it never reaches `BatchBisector` (phase 1 decision 5) |

### Third-review decisions (2026-10-11): correctness

| # | Decision |
|---|---|
| C14 | **Commit tokens replace the retry-inside-grace timing rule.** `parquet_commits(root_id, token, kind, committed_at)` gets a row in the same transaction as every collector commit (the flush's deduplication token) and every compaction swap (`compaction:<uuid>`). A retry after an unknown outcome asks `CommittedAsync(token)`: committed means done. Rows are kept `Catalog:CommitTokenHours` (24). The old Correctness 6 ("the whole retry schedule must fit in `GraceSeconds`") and `RegisteredKeysAsync`/`ExistsAsync` go: attempt duration no longer matters (R17) |
| C15 | **Staged rows make orphans a catalog query** (Quickwit's staged/published splits). Every object gets its catalog row, `state = staged`, **before** it is uploaded; the commit or swap turns it live. An object can then only exist without a row by a bug or a manual copy. Staged rows older than `Compaction:StagedTimeoutMinutes` (60) are abandoned: the compactor deletes the **row first, then the object**, and a commit turns only rows still staged live (count checked), so a slow commit that loses its rows fails as `Lost` and re-stages. The bucket listing becomes a safety net every `Compaction:BucketSweepDays` (7; 0 = off) that lists first, then reads the catalog, and deletes unnamed objects older than 24 h (R20, K2) |
| C16 | **Retention deletes the catalog's keys, not a prefix.** `ExpireDayAsync` marks the tenant-day's live rows expired and returns every expired key of that day; the sweeper deletes those keys in batches and forgets the rows. A file committed into the day afterwards is a new live row, which the next sweep expires: no live row ever points at a deleted object. Expired rows are deleted at once, not after the grace (the first version's `DueDeletionsAsync` said otherwise; R18) |
| C17 | **One root per storage location.** `parquet_roots` has a unique `(storage_kind, storage_location)`, and `EnsureRootAsync` refuses a location that is inside, or contains, another root's location (prefix nesting is checked in code). Two roots on one bucket prefix would otherwise sweep each other's objects (R19) |
| C24 | **The catalog is now the system of record**, and the README says so: losing it loses the root (live and superseded files cannot be told apart from the bucket alone). It must be backed up with the control plane, at the same recovery point. Every Parquet file carries key-value metadata (root, table, tenant, day, hour, level, token, writer, min/max time, rows) so a rebuild tool is possible later; the tool itself is out of scope (R24) |

### Third-review decisions (2026-10-11): cost

| # | Decision |
|---|---|
| C18 | **The small derived tables are inlined** (DuckLake's data inlining). `request_rollup_minute`, `log_rollup_minute` and `metric_catalog` are written as rows into catalog tables (`parquet_request_rollup`, `parquet_log_rollup`, `parquet_metric_catalog`, each with `root_id`) inside the flush's commit transaction (C13), not as files. When a day closes (`CloseDayGraceHours`) the compactor **moves** that tenant-day's rows into one `_day/` Parquet file per table: one transaction registers the file live and stamps the rows `moved_to_file_id`; readers exclude rows whose file is in their snapshot, and the rows are deleted after the grace. Late rows for a moved day are inlined again and moved by the next pass as an add-on file (decision 11's rules). The catalog therefore holds about a day of these rows, not the retention. `trace_index` (a row per trace) and `metric_series` (a row per series) stay files. `Catalog:InlineDerived` (default `true`) switches back to files per table; phase 0 spike 2 may set a provider's default to `false` if its reads miss O7 (K1) |
| C19 | **Object stores linger longer**: `LingerMilliseconds` 120,000 and `LateLingerMilliseconds` 300,000 when `Storage:Kind` is `AwsS3` or `AzureBlob` (Local keeps 30,000 and 120,000); a configured value wins. The price, stated in the provider README: data reaches the UI up to about two minutes after export, and a crashed collector loses up to its linger of buffered data (there is no write-ahead log). Phase 6 run 10 measures 30, 60 and 120 s (K1) |
| C20 | **Hour merges only on object stores**: `MinorFileThreshold` 60 and `MinorAgeSeconds` 0 (off) on object stores, so an open hour's ~30 writer files per table are rewritten once when the hour closes and once into the day, not three times; Local keeps 100 and 600 (replaces phase 1 decision 12's 20 and 120, which assumed a 30 s linger; K3) |
| C21 | **Tenant affinity is the documented deployment.** The provider works with any routing, but every collector that receives a tenant writes its own files per flush, so N collectors multiply that tenant's PUTs by N. The README shows consistent hashing on the API key at the load balancer (the bearer token is the tenant), and the harness measures with and without (phase 6 run 10). No code requirement (K1) |
| C22 | **A local read-through cache of compacted files** on the API host (O5 brought into scope). Level 1 and 2 files (hour and day merges, and moved inlined days) are immutable and read repeatedly: on a miss the query reads the remote URI and the cache downloads the file in the background; later reads get the local path. Writer files (level 0) are always read remote. `Storage:ReadCache:MaxBytes` (object stores 5 GiB, Local 0 = off), least recently used, under the work area; a file whose row is forgotten is evicted (K4) |
| C23 | **Cost is a goal with a number** (proposed): at the default settings, with tenant affinity, **at most 250 storage write requests per active tenant-hour** (ingest and compaction together) and **no routine bucket listing**. Phase 6 measures it and reports cost per tenant-month at the day's list prices. If it is missed, a collector write-ahead log (out of scope here) is the next lever, since it allows a linger of minutes (K6) |
| C25 | **Cloud deployment guidance** in the storage READMEs: hosts, bucket and catalog in **one region**; on AWS an **S3 gateway VPC endpoint** (a NAT gateway charges per GB both ways on every upload and read); on Azure a service endpoint, or a private endpoint with its per-GB charge stated; **S3 Standard / Blob Hot** for everything under a 90-day retention (Standard-IA's and Cool's 30-day minimum and IA's 128 KB minimum make rewritten or soon-deleted objects dearer); for longer retention a lifecycle rule on a tag the compactor sets on `_day/` files; a **flat-namespace** Azure account (hierarchical-namespace accounts bill operations differently and are untested here); versioning off; an incomplete-multipart expiry rule (K5) |

### Original decisions (2026-10-10), as they stand now

The numbers are the questions asked before the first version; "default" means the user accepted the proposed default. Where the
catalog or the third review changed one, the row says so.

| # | Decision |
|---|---|
| O1 | **Goal: multi-collector on cloud storage**, read latency close to local for a 24 h window (O7 makes "close" a number) |
| O2 | **v1 ships AWS S3 (with an endpoint override, used for MinIO in development and tests) and Azure Blob (flat namespace).** An ADLS Gen2 (hierarchical-namespace) account is not special-cased and **not tested** (Azurite does not emulate it); C25 recommends a flat-namespace account |
| O3 | **Local disk stays first-class**: it remains the default storage kind and keeps its tests; its phase 7 numbers are the regression baseline **for the read and write paths**, now measured with the catalog on its write path (phase 6 run 1 says what the catalog costs) |
| O4 | **Both hosts use the same store, the same storage configuration and the same catalog**; no mixed setups |
| O5 | **DuckDB reads remote objects directly** (`httpfs` for S3, `azure` for Blob). The interface hands DuckDB a *read URI*, so the local read-through cache (C22, now in scope) sits behind it without changing callers |
| O6 | **DuckDB extensions** are auto-installed by default, with a switch for an offline extension directory (`Storage:DuckDb:ExtensionDirectory`, `AutoInstallExtensions=false`) |
| O7 | **Latency target** (proposed by this plan, see [phase 6](phase-6-measurement.md)): with 25 ms injected round-trip time to the object store, trace list and trace detail p95 at most local p95 + 150 ms; 24 h dashboard summary at most 1 s p95 |
| O8 | ~~Writer identity without file locks~~ **Replaced by C12**: there is no writer identity to protect |
| O9 | ~~CAS on the lease object's ETag~~ **Replaced by C5**: the lease is a catalog row |
| O10 | **Commit latency and request count**: per-backend defaults are now set (C19 linger, C20 compaction thresholds); phase 6 confirms them. No structural change to ingestion beyond C6's unavailable outcome |
| O11 | **Temp files are local** on every backend (a local work directory); there is no remote `.tmp`. Abandoned multipart uploads are cleaned by a bucket lifecycle rule the README documents, not by code |
| O12 | **Credentials**: the platforms' default chains (AWS SDK credential chain; `DefaultAzureCredential`), plus optional explicit credentials (access key / connection string / SAS) from user secrets or environment, never appsettings |
| O13 | **Retention stays the sweeper** on every backend (it follows the control plane's windows). Lifecycle rules only for multipart and stray-object cleanup, and optional tiering of `_day/` files (C25) |
| O14 | **Encryption, policy and versioning are the bucket's.** The README says versioning should be off, or on with a noncurrent-version expiry (otherwise every delete keeps paying) |
| O15 | **The layout is unchanged** (C9), under a configurable key prefix |
| O16 | **Moving between backends** is documented: stop the hosts, copy the objects, update the root row's storage location, switch configuration; no tooling |
| O17 | **A small object-store primitive layer** under one shared `IParquetFileStore` implementation; local, S3 and Azure each implement only the primitives. With the catalog the layer has **no conditional operations** |
| O18 | ~~Partition enumeration by delimiter listings~~ **Replaced by the catalog**: partitions come from catalog rows; the bucket is listed only by the weekly safety-net sweep (C15) |
| O19 | **Emulators only**: MinIO and Azurite through Testcontainers, in the integration suite as their own provider names. **No real-cloud run.** |
| O20 | **Stress harness** runs Parquet on MinIO and Azurite (`--storage s3|azure`), with Toxiproxy in front for latency |
| O21 | **The cost model comes from measurement**: the store counts requests per operation and caller, the catalog counts statements, and the harness reports both per signal and per tenant-hour, against C23's target |
| O22 | **One package per storage backend**: `Keryhe.Telemetry.Parquet.Local`, `Keryhe.Telemetry.Parquet.AwsS3` and `Keryhe.Telemetry.Parquet.AzureBlob`, beside `Keryhe.Telemetry.Parquet`, which has no backend of its own. A consumer pulls in only the cloud SDK it uses. `Kind` values are `Local`, `AwsS3` and `AzureBlob`: the S3 backend targets AWS; its endpoint override (MinIO) is for development and testing |

### What "emulators only" leaves unproven

Said once here so the results do not overclaim:
- **Credential chains** (IAM role, instance profile, workload identity, managed identity) are never exercised; emulators use
  explicit keys. The code path is a single SDK call per backend, and the README tells operators how to check it.
- **Real latency and throughput.** Toxiproxy adds round-trip time but not S3's per-prefix request limits, throttling (`503 SlowDown`,
  Azure `503 ServerBusy`) or long-tail latency. Throttling is covered by tests with a fault-injecting store, not by a real service.
- **Real prices.** Request counts are measured; the dollar figures multiply them by list prices of the day.
- **ADLS Gen2 accounts** (hierarchical namespace): Azurite has none, so blob batch delete and listing behaviour there are untested.

## Review findings and what the catalog did to them

The first version was reviewed twice on 2026-10-11, for correctness (R) and performance (P). The table keeps them as a record.
**Resolved by the catalog** means the problem cannot arise any more; **applies** means the fix is still in this plan.

| # | Finding (short) | Now |
|---|---|---|
| R1 | A conditional PUT whose response is lost looks lost; the compactor would delete a published generation's files | **Applies in database form**: a commit or swap whose acknowledgement is lost is decided by its commit token (C14) |
| R2 | A cached listing older than a writer manifest hid pruned files | **Resolved**: no listing on reads; a compaction swaps inputs and outputs in one transaction |
| R3 | An hour's prefix vanishes once empty, hiding a merged day from readers, compactor and retention | **Resolved**: partitions come from catalog rows, never from listings |
| R4 | Retirement raced with a returning writer | **Resolved**: no manifests to retire |
| R5 | A retried flush overwrote a file readers could see | **Applies**: a committed token is never re-uploaded; a re-upload only overwrites a staged object (C14, C15) |
| R6, R7 | Writer-id conflicts: a same-id restart was refused, and a conflict was classed permanent and bisected | **Resolved**: no writer identity to conflict (C12). A catalog outage is unavailable (C6) and never bisected |
| R8 | DuckDB secrets expire under temporary credentials; S3 needs `REGION`, Azure's chain `ACCOUNT_NAME` | **Applies** ("DuckDB"; phases 3, 4) |
| R9 | S3 conditional-write status codes | **Resolved**: no conditional operations. Plain errors are still classified per backend |
| R10 | Heartbeats could not use Local's lock file | **Resolved**: heartbeats are catalog rows; the lock files are gone |
| R11 | Work-area clean-up by writer id leaked, unfiltered clean-up deleted live files | **Applies**: a per-process subdirectory with a held lock file (phase 2) |
| R12 | Heartbeat age compared two machines' clocks | **Resolved**: the catalog stamps heartbeats, leases, staging and supersession with **database time** |
| R13 | Retirement used the wrong `through` for covered hours | **Resolved**: no `through` |
| R14 | Remote contract tests need Docker | **Applies**: remote `ObjectStoreContractTests` and the catalog's provider tests live in `IntegrationTests` |
| R15 | Request count, `WorkPath` default, step order | **Applies** where still relevant (commit cost: "Cost"; `WorkPath`: "Configuration") |
| P1 | Compaction passes listed and read every partition every minute | **Resolved**: the compactor finds its work with catalog queries; the bucket is listed only by the weekly safety net (C15) |
| P2 | Two listings and W+1 reads per partition | **Resolved**: one catalog query per tenant-day, cached (C10) |
| P3 | Unhinted trace detail resolved the whole retention | **Partly**: resolving is now one query, but DuckDB still opens every live `trace_index` file in the window; unhinted lookups go newest day first and stop at the first hit (phase 5), and `trace_index` is already sorted by `(trace_hi, trace_lo)` so a file costs its footer plus one row group |
| P4 | Old hours stayed expensive while a writer lived | **Resolved**: a closed day is its live files, nothing else |
| P5 | A commit was ~7 sequential requests | **Resolved**: a flush is two catalog statements (stage, commit) around its concurrent uploads |
| P6 | Minor-compaction thresholds tuned for local disk | **Applies**, now C20 |
| P7 | DuckDB footer cache, external file cache, threads | **Applies** ("DuckDB", phase 5) |
| P8 | One request budget shared by reads and background work | **Applies** (`MaxBackgroundRequests`) |
| P9 | A late file rewrote the whole merged day | **Applies**: minor day merge (phase 1 decision 11) |
| P10 | The stress observer walked the bucket every 5 s | **Applies, simpler**: it counts files and rows from the catalog |
| P11 | Retention read manifests to count rows | **Resolved**: `SUM(row_count)` in the catalog |
| P12 | The cost arithmetic did not say where requests go | **Applies** ("Cost" below, phase 6) |

### Third review (2026-10-11): correctness, performance and cost of the catalog design

| # | Finding (short) | Now |
|---|---|---|
| R16 | C6 promised held buffers, but `PartitionedIngestionWorker` drops a batch after `MaxFlushRetries` (~25 s of backoff); phase 6 run 6c would fail | C6 rev.: `FlushErrorKind.Unavailable`, retried without the cap |
| R17 | The retry-inside-grace rule counted backoff only, not attempt duration, and conflicts with holding | C14: commit tokens |
| R18 | Retention's prefix delete could remove a file committed into the day after `ExpireDayAsync`, leaving a live row on a missing object; and the README and phase 1 disagreed on whether expired files wait for the grace | C16: delete by key, at once |
| R19 | Two roots on one bucket prefix sweep each other's objects as orphans | C17 |
| R20 | A one-hour orphan age is short against lease, holds and remote day merges (Iceberg defaults to 3 days); list/catalog order unstated | C15: staged rows; listing a weekly safety net at 24 h |
| R21 | Per-(table, tenant, day) cache entries expire independently, so a request could see derived rows without their raw rows | C10 rev.: one snapshot per tenant-day |
| R22 | Nothing checked that superseded files outlive a cached list plus a query | Correctness 3: startup requires `GraceSeconds` ≥ `CacheSeconds` + 60 |
| R23 | MySQL `INSERT IGNORE` hides truncation and other errors; the lease's insert-if-absent races | Phase 1 decision 2 |
| R24 | Losing the catalog loses the root | C24 |
| P13 | The read index put `state` after the date range, so live reads also walked superseded rows; no index for cross-tenant compaction queries | "The catalog" indexes (partial on live rows where the provider has them) |
| P14 | AWS SDK's default flexible checksums (2025) are rejected by some S3-compatible servers, older MinIO among them | Phase 0 spike 1, `AwsS3:ChecksumMode` |
| P15 | ADLS Gen2 claimed to work without a test | O2 rev., "unproven" |
| K1 | ~12 objects per tenant per collector per 30 s: ~1,440 PUTs per tenant-hour, ~$5 per tenant per collector per month in PUTs alone at S3 list prices; N collectors per tenant multiply it | C18, C19, C21 |
| K2 | The hourly orphan sweep lists every hour folder of the scanned days: ~57,000 LISTs per pass at 100 tenants | C15 |
| K3 | Minor merges rewrote each byte three times | C20 |
| K4 | Repeated reads of closed hours and days paid GETs and latency every time | C22 |
| K5 | NAT gateway, cross-region egress and cold tiers can exceed the request bill | C25 |
| K6 | Cost was measured but not a goal | C23 |

## What changes, in one picture

```
                    today                                           after
ParquetBulkWriter ─┐                              ParquetBulkWriter ─┐                  ┌─ IParquetCatalog ── <Provider>ParquetCatalog
ParquetCompactor  ─┼─ IParquetFileStore           ParquetCompactor  ─┼──────────────────┤   (files, commits, inlined     (PostgreSQL,
ParquetFileResolver┤   (paths, rename,            ParquetFileResolver┤                  │    rollups + metric catalog,    SqlServer, MySql)
RetentionSweeper  ─┘    manifests, dirs)          RetentionSweeper  ─┘                  │    lease, writers, root)
ParquetWriterIdentity ─ lock files                                                      └─ IParquetFileStore ── ObjectParquetFileStore ─ IObjectStore ─┬─ LocalObjectStore
ParquetSchemaFile    ─ _schema.json               writer registry, lease, root marker ─ catalog   (keys, read URIs,                         ├─ AwsS3ObjectStore
DuckDB ─ local paths                              DuckDB ─ read URIs (+ read cache) + IDuckDbStorageSetup     temp→upload)                       └─ AzureBlobObjectStore
```

## Design

### Packages and registration

| Package | Contains | Brings |
|---|---|---|
| `Keryhe.Telemetry.Core` | `IParquetCatalog` and its models (`CatalogFile`, `CatalogCommit`, `CatalogCompaction`, the inlined row types, ...), so provider packages can implement it without referencing the Parquet package or DuckDB, as the control-plane interfaces already live in Core; `FlushErrorKind.Unavailable` (C6) | nothing new |
| `Keryhe.Telemetry.PostgreSQL`, `.SqlServer`, `.MySql` | `<Provider>ParquetCatalog : IParquetCatalog` and `Add<Provider>ParquetCatalogServices(configuration)`, beside their control-plane classes. PostgreSQL registers its own **keyed** `NpgsqlDataSource` (`"ParquetCatalog"`) even when the string equals the control plane's | their existing driver |
| `Keryhe.Telemetry.Parquet` | Everything backend-neutral: the bulk writer, compactor, resolver, read engine, read cache, retention sweeper; the seam (`IObjectStore`, `ObjectParquetFileStore`, `IDuckDbStorageSetup`, `IStorageErrorClassifier`, `IParquetStorageBackend`, `StorageOptions`), all public so a backend can live in another package. No backend of its own | DuckDB, as today |
| `Keryhe.Telemetry.Parquet.Local` | `LocalObjectStore`, the no-op DuckDB setup, the per-partition `.tmp` work area (so a commit stays a rename) | none |
| `Keryhe.Telemetry.Parquet.AwsS3` | `AwsS3ObjectStore`, `AwsS3DuckDbStorageSetup` (`httpfs`), the S3 error classifier, `AwsS3StorageOptions` | `AWSSDK.S3` |
| `Keryhe.Telemetry.Parquet.AzureBlob` | `AzureBlobObjectStore`, `AzureBlobDuckDbStorageSetup` (`azure` extension), the Blob error classifier, `AzureBlobStorageOptions` | `Azure.Storage.Blobs`, `Azure.Identity` |

The host registers the Parquet services, exactly one storage backend and exactly one catalog provider:

```csharp
case "Parquet":
    builder.Services.AddParquetCollectorServices(builder.Configuration);
    switch (builder.Configuration["Telemetry:Parquet:Storage:Kind"] ?? "Local")
    {
        case "Local":     builder.Services.AddParquetLocalStorage(builder.Configuration);     break;
        case "AwsS3":     builder.Services.AddParquetAwsS3Storage(builder.Configuration);     break;
        case "AzureBlob": builder.Services.AddParquetAzureBlobStorage(builder.Configuration); break;
        default: throw new InvalidOperationException(
            "Unknown Telemetry:Parquet:Storage:Kind (expected Local, AwsS3 or AzureBlob).");
    }
    switch (builder.Configuration["Telemetry:Parquet:Catalog:Provider"] ?? builder.Configuration["ControlPlane:Provider"])
    {
        case "PostgreSQL": builder.Services.AddPostgreSqlParquetCatalogServices(builder.Configuration); break;
        case "SqlServer":  builder.Services.AddSqlServerParquetCatalogServices(builder.Configuration);  break;
        case "MySql":      builder.Services.AddMySqlParquetCatalogServices(builder.Configuration);      break;
        default: throw new InvalidOperationException(
            "Unknown Telemetry:Parquet:Catalog:Provider (expected PostgreSQL, SqlServer or MySql; defaults to ControlPlane:Provider).");
    }
    break;
```

Each `AddParquet<Backend>Storage` registers `IObjectStore`, `IDuckDbStorageSetup`, its `IStorageErrorClassifier` (consulted by the
core's `ParquetFlushErrorClassifier`), its options with their `Validate()` and a marker `IParquetStorageBackend { Kind }`. A
startup validator in the core fails when no backend is registered, more than one is, the marker's `Kind` differs from the
configured `Kind`, or no `IParquetCatalog` is registered, naming the missing call. The catalog's connection string is read
through a `ParquetCatalogConnection.FromConfiguration` (as `ControlPlaneConnection` does): `ConnectionStrings:Catalog`, else
`ConnectionStrings:ControlPlane`, else a startup failure naming both keys. The per-backend defaults of C19, C20 and C22 are applied
from the registered `Kind` before options validation, so a configured value always wins.

A consumer references `Keryhe.Telemetry.Parquet`, one storage package and one relational provider package (which serves both the
control plane and the catalog).

Who references the storage packages: `Collector.Server` and `Api.Server` (all three, for the switch), `Keryhe.Telemetry.Parquet.Tests`
(Local only: it is the no-Docker suite, with an in-memory `IParquetCatalog` fake; the abstract `ObjectStoreContractTests` and
`ParquetCatalogContractTests` and their in-process subclasses live there), `IntegrationTests` (AwsS3 and AzureBlob, and the three
real catalogs: the MinIO, Azurite and per-provider subclasses), `TestInfrastructure` (Local for `ParquetProviderContainer`, AwsS3
for `ParquetS3ProviderContainer`, AzureBlob for `ParquetAzureProviderContainer`) and `StressTests` (all three).

`Kind=AwsS3` targets AWS. Its `ServiceUrl`/`ForcePathStyle` override, which points it at MinIO, is how the tests, emulator fixtures
and stress harness run it; other S3-compatible stores (R2 and the like) may work through it but are not a supported target.

### The catalog (`parquet-catalog` schema component)

Tables (names prefixed so they can share the control-plane database; types per provider as the other scripts do):

| Table | Columns | Keys and indexes |
|---|---|---|
| `parquet_roots` | `root_id`, `telemetry_version`, `tables_hash`, `storage_kind`, `storage_location` (bucket/container + prefix, or the local path), `created_at` | PK `root_id`; **unique `(storage_kind, storage_location)`** (C17) |
| `parquet_files` | `id` (identity), `root_id`, `table_name`, `tenant_id`, `file_date`, `file_hour` (0-23, NULL for a `_day/` file), `object_key`, `token` (the flush token or `compaction:<uuid>`), `writer_id`, `level` (0 writer file, 1 hour-compacted, 2 day), `state` (0 live, 1 superseded, 2 expired, 3 staged), `min_time`, `max_time` (the table's partition time, Unix ns), `row_count`, `byte_count`, `staged_at`, `committed_at`, `superseded_at` | PK `id`; **unique `(root_id, object_key)`**; reads: `(root_id, tenant_id, file_date)` **covering** (`table_name`, `file_hour`, `level`, `object_key`, `min_time`, `max_time`, `row_count`) and **filtered to `state = 0`** on PostgreSQL (partial) and SQL Server (filtered, `INCLUDE`); compaction: `(root_id, file_date, table_name, tenant_id) WHERE state = 0`; staged clean-up: `(root_id, staged_at) WHERE state = 3`; deletes: `(root_id, superseded_at) WHERE state = 1`. **MySQL** has no partial indexes: `state` leads instead (`(root_id, state, tenant_id, file_date, table_name)`, `(root_id, state, file_date)`, `(root_id, state, staged_at)`, `(root_id, state, superseded_at)`). Phase 0 spike 2 checks every plan |
| `parquet_commits` | `root_id`, `token`, `kind` (`flush`/`compaction`), `committed_at` | PK `(root_id, token)`; `(committed_at)` for pruning (C14) |
| `parquet_request_rollup` | `root_id`, `id`, the Parquet `request_rollup_minute` columns (tenant, service, minute, operation, counts, duration sum/max, `h00`..`h23`), `moved_to_file_id` | `(root_id, tenant_id, bucket_start_unix_nano, service_name)` (C18) |
| `parquet_log_rollup` | `root_id`, `id`, the Parquet `log_rollup_minute` columns, `moved_to_file_id` | `(root_id, tenant_id, bucket_start_unix_nano, service_name)` |
| `parquet_metric_catalog` | `root_id`, `id`, the Parquet `metric_catalog` columns, `catalog_date`, `moved_to_file_id` | `(root_id, tenant_id, catalog_date)` |
| `parquet_writers` | `root_id`, `writer_id`, `host`, `role` (`collector`/`api`), `started_at`, `last_seen_at` | PK `(root_id, writer_id)` |
| `parquet_leases` | `root_id`, `name` (`compaction`), `owner`, `expires_at` | PK `(root_id, name)` |
| `parquet_catalog_schema_version` | as the other components | |

On SQL Server and MySQL the clustered key of `parquet_files` and the three inlined tables leads with their read path
(`(root_id, tenant_id, file_date, id)` and `(root_id, tenant_id, bucket_start_unix_nano, id)`), as the telemetry scripts already do
for `spans`, so commits for different tenants append in different places and reads are range scans. `parquet_files` churns
(insert, supersede, delete) all day: the PostgreSQL script sets the table's autovacuum scale factors low, and the provider README
says so.

Every time the catalog stores (`staged_at`, `committed_at`, `superseded_at`, `last_seen_at`, `expires_at`, `created_at`) is **the
database's clock** (`now()`, `SYSUTCDATETIME()`, `UTC_TIMESTAMP(6)`), and every age is compared in SQL against it, so no two
machines' clocks have to agree (R12).

`IParquetCatalog` (Core) operations, one round trip each unless noted:

| Operation | What it does |
|---|---|
| `EnsureRootAsync(root)` / `VerifyRootAsync(root)` | Insert the root row if absent (collector) and compare version, tables hash, kind and location, refusing a location another root holds or nests with (C17); the API host only verifies. A root whose objects were written by the manifest-based provider has no row, and its bucket or folder holds `_schema.json`: refused with the C7 message |
| `StageAsync(token, files)` | Insert staged rows for a flush's or compaction's objects before upload; a row already staged under the same key (a retry) has its `staged_at` refreshed (C15) |
| `CommitAsync(token, files, inlined)` | One transaction (C13, C14): insert the `parquet_commits` row (a duplicate token rolls back as `AlreadyCommitted`), turn the token's staged rows live (count checked; a missing row rolls back as `Lost`, C15), insert the inlined rows (C18) |
| `CommittedAsync(token)` | Whether a token committed (an unknown outcome, C14) |
| `LiveFilesAsync(tenant, fromDay, toDay)` | Live files of every table of a tenant in a day range, one snapshot (C10; readers cache per tenant-day) |
| `InlinedRollupAsync(...)`, `InlinedMetricCatalogAsync(...)` | The inlined rows a read needs, excluding rows whose `moved_to_file_id` is in the reader's snapshot (C18) |
| `CompactionCandidatesAsync(...)` | The partitions with work to do (phase 1 decision 8), aggregated in SQL, including tenant-days whose inlined rows are due to move |
| `SwapAsync(id, inputs, outputs)` | One transaction: the `parquet_commits` row, outputs staged → live, inputs live → superseded **only if all are still live** (count checked); otherwise roll back and report `Lost`. The inlined move is the same call with rows instead of input files (stamps `moved_to_file_id`) |
| `DueDeletionsAsync(grace, limit)` / `ForgetAsync(ids)` | Superseded files and moved inlined rows whose grace has passed; then their rows |
| `AbandonStagedAsync(olderThan, limit)` | Deletes staged rows older than `StagedTimeoutMinutes` and returns their keys (`DELETE ... RETURNING` / `OUTPUT`; MySQL selects `FOR UPDATE` then deletes in one transaction); the caller then deletes the objects (C15) |
| `ExpiredDaysAsync(table, latestCutoffDay)` / `ExpireDayAsync(table set, tenant, day)` | Retention: the tenant-days before a cutoff, their raw row count, marking live rows expired and returning every expired key of the day (C16); the inlined rows of the day are deleted in the same transaction |
| `PruneCommitsAsync(hours)` | Deletes commit tokens older than `CommitTokenHours` (C14) |
| `RegisterWriterAsync` / `HeartbeatAsync` | The writer registry (C12) |
| `TryAcquireLeaseAsync(owner, seconds)` / `ReleaseLeaseAsync` | One conditional `UPDATE ... WHERE owner = @me OR expires_at < now`; when it touches no row and none exists, an insert, treating a duplicate-key error as "another holder won" |

The catalog is **the telemetry side's** metadata even when it shares the control-plane database: nothing joins it to the
control-plane tables, and retention takes its tenants from `parquet_files` and the inlined tables, never from `tenants`, so a
deleted tenant's data still expires.

### Correctness

1. **A file is visible only once the catalog lists it as live.** Stage its row, upload, commit (C15). An upload with no
   commit leaves a staged row, which the compactor abandons after `StagedTimeoutMinutes`, row first, then object.
2. **A compaction is atomic.** Its outputs become live and its inputs superseded in one transaction, which fails if any input
   is no longer live (another compactor, or retention, got there first). Each row is therefore in exactly one live file at any
   committed state, and two racing compactors cannot both win. The lease (C5) makes the race rare; the transaction makes it
   harmless. The inlined move (C18) is the same: the day file goes live and its rows are stamped in one transaction.
3. **A superseded file is deleted only after the grace** (`Compaction:GraceSeconds`, measured in database time), so a reader
   holding a list from before the swap can still read it, as today. Startup fails unless `GraceSeconds` ≥ `Catalog:CacheSeconds`
   + 60 (R22). A query or export running longer than the grace may still fail on a deleted file (unchanged from local).
4. **A lost acknowledgement is decided by its token** (R1, C14). A collector or compactor whose commit's outcome is unknown asks
   `CommittedAsync(token)`: committed means done (a compactor keeps its outputs); not committed means retry (a compactor whose
   swap did not commit deletes its outputs' staged rows and objects).
5. **A visible object is never overwritten** (R5). An object key belongs to one token; a committed token is never uploaded again;
   a retry of an uncommitted token re-stages and re-uploads, overwriting only staged objects nobody can read.
6. **A slow commit cannot resurrect an abandoned object.** The clean-up deletes the staged row before the object, and a commit
   turns only rows still staged live, checking the count. A commit that lost a row rolls back as `Lost` and the flush re-stages
   and re-uploads (C15).
7. **Retention never leaves a live row on a missing object** (C16). It deletes exactly the keys it expired; anything committed
   into the day later is a live row the next sweep expires.
8. **Inlined rows are counted once** (C18). A reader excludes the rows whose `moved_to_file_id` is in its snapshot and reads
   that file instead; a reader whose snapshot predates the move reads the rows and not the file. The rows are deleted after the
   grace, like a superseded file.

### Reads

`ParquetFileResolver` asks the catalog, not the bucket: `LiveFilesAsync` for the scope's tenant and day range, every table in one
snapshot, filtered in memory to the scope's tables and to the files whose `[min_time, max_time]` overlaps the scope (and, for a
`_day/` file, the hours the scope covers). The answer is cached per tenant-day for `CacheSeconds` (C10), so scopes that differ in
their tables or time bounds within a day share entries. The compactor and retention read uncached.

- **Inlined tables** (C18): the rollup reads (dashboard, trace list and logs cards, error-rate and log-spike alerts), the
  per-operation figures, the metrics catalog and the service list read the moved `_day/` files through DuckDB and the unmoved
  rows through the catalog connection, and fold both (rollup rows are partial sums already, so the fold is the existing one).
  A 24 h dashboard is therefore at most two day files plus a day of rows in one indexed range.
- **Read cache** (C22): the read URI of a level 1 or 2 file is the cached local path when present; a miss reads remote and
  queues a background download bounded by `MaxBackgroundRequests`.
- **Wide scopes** (P3): a request's sliced reads reuse the days they already resolved; unhinted trace detail looks `trace_index`
  up newest day first in widening chunks (1, 3, 7 days, then the rest) and stops at the first chunk that finds the trace.
- **DuckDB caches** (P7): `parquet_metadata_cache=true` (footers; safe because a visible file is never overwritten),
  `enable_http_metadata_cache=true`, and the external file cache, if the bundled DuckDB has it, sized explicitly inside
  `memory_limit`; `threads` defaulting to 4 x cores on object stores, since remote reads wait on I/O.
- **Counted**: every store call on the `Keryhe.Telemetry.Parquet.Storage` meter (`storage_requests{backend, op, outcome,
  caller}`, `storage_request_duration`, `storage_bytes{direction}`, `read_cache{result}`) and every catalog statement on
  `Keryhe.Telemetry.Parquet.Catalog` (`catalog_statements{op, outcome}`, `catalog_statement_duration{op}`, `catalog_cache{result}`),
  which is how O21's cost model is built.

### DuckDB

`IDuckDbStorageSetup`, one per backend, gives the SQL each new DuckDB database runs: `LOAD` the extension (or `INSTALL` then `LOAD`
when auto-install is on), `SET extension_directory`, and `CREATE SECRET` (S3: `REGION` always, then `PROVIDER credential_chain`,
or `KEY_ID`/`SECRET`, plus `ENDPOINT`/`URL_STYLE 'path'`/`USE_SSL` for MinIO; Azure: `PROVIDER credential_chain` with
`ACCOUNT_NAME` taken from `ServiceUri`, or `CONNECTION_STRING`). The read engine, the compactor's database and the stress observer
all go through it. The .NET SDK and DuckDB resolve credentials separately; phase 0 checks that one configuration feeds both.

**Credentials expire** (R8). The read engine is one DuckDB database for the life of the process, and an IAM role, IRSA,
workload or managed identity hands out temporary credentials. So `IDuckDbStorageSetup` owns the refresh: the secret uses the
extension's own refresh (`REFRESH auto` on the S3 `credential_chain`, if the bundled DuckDB has it; phase 0 spike 3), otherwise
the setup recreates it (`CREATE OR REPLACE SECRET`) from what the SDK resolved, before the credentials' expiry and on an
authentication error, retrying the statement once. Static keys and connection strings need no refresh.

Writes never go through DuckDB to the store: DuckDB `COPY`s to the local work directory (with the C24 key-value metadata) and
`PutFileAsync` uploads, so Azure's write support in the extension does not matter and every write takes the same counted path.

### The primitive layer (`IObjectStore`)

Keys are `/`-separated, relative to the configured prefix. Every operation is async and cancellable. With the catalog there are
**no conditional operations**: the bucket is written once per key and deleted, never compared-and-swapped.

| Primitive | Local | S3 | Azure Blob |
|---|---|---|---|
| `PutFileAsync(key, localPath)` | rename into place (same volume) or copy | `TransferUtility` (multipart over `MultipartThresholdBytes`) | `Upload` with `StorageTransferOptions` |
| `GetFileAsync(key, localPath)` | copy | `GetObject` to a file | `DownloadTo` (the read cache, C22) |
| `ListAsync(prefix, delimiter?)` → objects (name, size, modified) and common prefixes | directory enumeration | `ListObjectsV2` (paged) | `GetBlobsByHierarchy` (paged) |
| `DeleteAsync(key)` | `File.Delete` | `DeleteObject` | `DeleteIfExists` |
| `DeleteManyAsync(keys)` → count | `File.Delete` each | `DeleteObjects` in 1,000-key batches | batch delete (256 per batch) |
| `ExistsAsync(prefix)` | directory exists | `ListObjectsV2` with `MaxKeys=1` | `GetBlobsByHierarchy` first page |
| `ReadUri(key)` | absolute path | `s3://bucket/prefix/key` | `az://container/prefix/key` |

`ListAsync` is used only by the weekly safety-net sweep (C15) and by `VerifyRootAsync`'s check for a manifest-era root (C7).
Retention deletes keys with `DeleteManyAsync` (C16); there is no prefix delete.

### Configuration

`Telemetry:Parquet` (existing keys unchanged unless listed):

| Key | Package | Default | Meaning |
|---|---|---|---|
| `RootId` | core | (required) | The root's name in the catalog (C11) |
| `WriterId` | core | | **Removed** (C12) |
| `LingerMilliseconds`, `LateLingerMilliseconds` | core | object stores 120,000 and 300,000; Local 30,000 and 120,000 | C19 |
| `Catalog:Provider` | host switch | `ControlPlane:Provider` | `PostgreSQL`, `SqlServer` or `MySql` (C3) |
| `Catalog:CacheSeconds` | core | 5 | Live-file snapshot cache per tenant-day (C10) |
| `Catalog:InlineDerived` | core | `true` | Rollups and metric catalog as catalog rows until their day closes (C18) |
| `Catalog:CommitTokenHours` | core | 24 | How long commit tokens are kept (C14) |
| `Catalog:HeartbeatSeconds` | core | 30 | Writer registry heartbeat (C12) |
| `Catalog:WriterRowRetentionDays` | core | 7 | Writer rows not seen for this long are deleted by the compactor |
| `Storage:Kind` | host switch | `Local` | `Local`, `AwsS3` or `AzureBlob` |
| `Storage:Prefix` | core | (empty) | Key prefix inside the bucket/container |
| `Storage:WorkPath` | core | `Local`: `<RootPath>/_tmp`; object stores: `<system temp>/keryhe-parquet` | Local work directory (temp Parquet files, DuckDB spill, the read cache). Each process works in its own subdirectory `<host>-<role>-<start id>` holding a lock file (R11). For `Local`, `RootPath` stays the root and data temp files stay in each partition's `.tmp` |
| `Storage:MaxConcurrentRequests` | core | 32 | Per host, for reads and ingestion (P8) |
| `Storage:MaxBackgroundRequests` | core | 8 | Per host, for the compactor, clean-up, retention and read-cache downloads (P8) |
| `Storage:ReadCache:MaxBytes` | core | object stores 5 GiB; Local 0 (off) | C22 |
| `Storage:DuckDb:AutoInstallExtensions`, `Storage:DuckDb:ExtensionDirectory` | core | `true`, DuckDB's default | Offline deployments set both |
| `DuckDb:Threads` | core | object stores: 4 x cores; Local: cores | Remote reads are I/O-bound (P7) |
| `Compaction:StagedTimeoutMinutes` | core | 60 | Staged rows older than this are abandoned (C15) |
| `Compaction:BucketSweepDays` | core | 7 (0 = off) | Safety-net listing for objects no row names (C15); replaces `OrphanSweepMinutes` |
| `Compaction:MinorFileThreshold`, `MinorAgeSeconds` | core | object stores 60, 0 (off); Local 100, 600 | C20 |
| `Compaction:DayAddOnMaxFiles`, `DayAddOnMaxRows` | core | 8, 5,000,000 | Minor day merge (P9) |
| `Storage:AwsS3:Bucket`, `Region`, `ServiceUrl`, `ForcePathStyle` | AwsS3 | (required), SDK default, AWS, `false` | `ServiceUrl` + `ForcePathStyle=true` points it at MinIO |
| `Storage:AwsS3:AccessKeyId`, `SecretAccessKey` | AwsS3 | (none: credential chain) | Secrets/environment only |
| `Storage:AwsS3:MultipartThresholdBytes` | AwsS3 | 64 MiB | |
| `Storage:AwsS3:ChecksumMode` | AwsS3 | SDK default on AWS; `WhenRequired` when `ServiceUrl` is set | P14; phase 0 spike 1 confirms against MinIO |
| `Storage:AzureBlob:ServiceUri`, `Container` | AzureBlob | (required) | `https://<account>.blob.core.windows.net` |
| `Storage:AzureBlob:ConnectionString` | AzureBlob | (none: `DefaultAzureCredential`) | Secrets/environment only; Azurite uses it |
| `Storage:AzureBlob:MultipartThresholdBytes` | AzureBlob | 64 MiB | |

`ConnectionStrings:Catalog` (default `ConnectionStrings:ControlPlane`) on both hosts, secrets or environment only. Each package
validates its own keys; the core validates `RootId` (required, `^[A-Za-z0-9_.-]{1,64}$`), `GraceSeconds` against `CacheSeconds`
("Correctness" 3), `StagedTimeoutMinutes` against `LeaseSeconds` (it must be longer, so a live compaction's staged outputs are not
abandoned), and the backend and catalog registration.

## Cost

Requests dominate an object-store bill; storage (zstd Parquet) and GETs are small beside PUTs and LISTs. The estimate that drove
the third review, for 100 tenants and 2 collectors at 30-day retention, at S3 Standard list prices of the time (PUT/LIST about
$0.005 per 1,000, GET about $0.0004 per 1,000; Azure Blob Hot on a flat namespace is the same order). **Estimates, not
measurements**; phase 6 replaces them:

| | The plan before the third review | This plan (C15, C18-C21) |
|---|---|---|
| Objects per tenant flush | ~12 (raw, `trace_index`, both rollups, up to 5 point tables, `metric_catalog`, `metric_series`) | ~6-7 (raw, `trace_index`, the point tables present, `metric_series`) |
| Flushes per tenant-hour | 120 per collector receiving the tenant (30 s), x 2 collectors | 30 (120 s), one collector (C21) |
| Ingest PUTs per tenant-hour | ~2,900 | ~200 |
| Compaction | minor + hour + day rewrites | hour + day rewrites, inlined days moved once a day |
| Routine LISTs | ~57,000 per hourly sweep | none (weekly safety net) |
| Rough monthly request cost | ~$1,500 | ~$100-150 |

The levers left, in order, if C23 is missed: a longer linger (data freshness and crash loss grow with it), fewer collectors per
tenant, and a collector write-ahead log (out of scope) that allows a linger of minutes, as Tempo and Mimir do. Network charges
(C25) are outside this table and can exceed it when hosts reach the store through a NAT gateway or across regions.

## Plans, in order

| Phase | Plan | Items | Size |
|---|---|---|---|
| 0 | [phase-0-spikes.md](phase-0-spikes.md) | DuckDB `httpfs`/`azure` against MinIO/Azurite, the AWS SDK checksum setting and credential refresh; offline extensions; catalog statement cost per provider, inlined rollup reads included; Testcontainers modules | Small. Throwaway code, one results file |
| 1 | [phase-1-catalog.md](phase-1-catalog.md) | The `parquet-catalog` schema component on three providers; `IParquetCatalog` and its implementations; staged commits with tokens, inlined derived tables, the unavailable outcome; writer, resolver, compactor, retention, lease, writer registry and root marker on the catalog; manifests removed. **Still on today's local file store** | Large |
| 2 | [phase-2-seam.md](phase-2-seam.md) | `IObjectStore`, `ObjectParquetFileStore`; the `Keryhe.Telemetry.Parquet.Local` package; backend registration, per-backend defaults and their validator; the per-process work area; read URIs. No behaviour change on local | Medium |
| 3 | [phase-3-s3.md](phase-3-s3.md) | The `Keryhe.Telemetry.Parquet.AwsS3` package; MinIO fixture runs the shared suites (`Provider=ParquetS3`) | Medium |
| 4 | [phase-4-azure.md](phase-4-azure.md) | The `Keryhe.Telemetry.Parquet.AzureBlob` package; Azurite fixture (`Provider=ParquetAzure`) | Medium |
| 5 | [phase-5-read-path-and-ops.md](phase-5-read-path-and-ops.md) | Read cache, DuckDB settings, request budgets, wide scopes, meters; stress harness `--storage`, collectors and tenant affinity, the catalog-based observer; docs, cost and deployment guidance | Medium |
| 6 | [phase-6-measurement.md](phase-6-measurement.md) | Catalog load, request counts against C23 and the cost model, latency under Toxiproxy, contention, local regression | Medium. Runs, not code |

Why this order:
- Phase 1 replaces the metadata protocol on local disk alone, before any storage abstraction or cloud code exists, so a
  regression is attributable to the catalog and is tested by every existing Parquet suite.
- Phase 2 is then a smaller refactor: with no manifests left there is less to move behind the store.
- S3 before Azure: MinIO is the more complete emulator, and the second backend then only fills in primitives.
- Phase 5's caches and budgets are tuned by request and statement counts, which need the meters and a backend.

## Risks

| Risk | Mitigation |
|---|---|
| The catalog database is now on the write path of every Parquet collector, Local included | C6: hold and retry with the existing backpressure; readiness reports it. The README says to size it like the control plane, or give it its own database (C3) |
| Losing the catalog database loses the root | C24: backed up with the control plane; files carry enough metadata for a later rebuild tool |
| Catalog load: two statements per flush per collector, a read per tenant-day per cache period per API host, inlined rows (about a day of rollups per tenant), the compactor's queries | One transaction per flush (C13), the read cache (C10), compaction work found by aggregate queries, inlined rows moved out daily (C18); phase 0 spike 2 and phase 6 measure statements per second, row counts and latency per provider; `InlineDerived=false` is the fallback per provider |
| `parquet_files` churn | Superseded rows are forgotten after the grace; a closed day keeps a few live rows per table and tenant; partial/filtered indexes on live rows; autovacuum settings on PostgreSQL |
| Freshness: data reaches the UI up to ~2 minutes after export on object stores, and a crash loses up to the linger | C19 states it; phase 6 run 10 measures 30/60/120 s so the default is a measured choice; a write-ahead log is the out-of-scope fix |
| A hold (C6) keeps the collector refusing exports for as long as the catalog or store is down | That is the intended backpressure: clients retry with `RetryInfo`; readiness takes the instance out of rotation; memory is bounded by the gates |
| Object-store reads are slower per file than local | Compaction keeps a closed day to a few files; the read cache (C22); DuckDB's footer cache (P7); phase 6 run 4 against O7 |
| PUT cost on a many-tenant deployment | C18-C21; C23's target, measured per tenant-hour in phase 6; the README gives the arithmetic |
| A long export reads a file deleted after the grace | Unchanged from local (documented) |
| Emulator behaviour differs from the real service | Listed under "What emulators only leaves unproven" |
| DuckDB extension and DuckDB.NET versions must match (`DuckDB.NET.Data.Full` 1.5.5) | Phase 0 spike 1 pins the extension version; the README says how to fetch extensions for an offline install |

## Out of scope

- A collector write-ahead log (the next cost lever if C23 is missed: it allows a linger of minutes without more crash loss).
- A catalog rebuild tool (C24 makes one possible).
- Importing a manifest-based root (C7).
- A tenant-first key layout (C9).
- Google Cloud Storage, and other S3-compatible stores as production targets.
- Real-cloud runs (O19), cross-region setups.
- Querying the bucket from outside the API (the layout is still not a public contract).
- Relaxing Local's one-machine rule: the catalog removes the stale-manifest failure that caused it, but a shared file system is
  not re-tested here.
