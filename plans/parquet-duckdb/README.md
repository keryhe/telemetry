# Parquet + DuckDB provider

Written 2026-10-09. Step 4 of the provider reshape (control-plane split, Timescale removal and the ClickHouse redesign
are done). Adds a fifth telemetry provider, `Database:Provider = "Parquet"`: the collector writes telemetry as Parquet
files and the API reads them with an in-process DuckDB. It is the cheap, simple, file-based option: no database server
for telemetry. The control plane stays relational (PostgreSQL, SQL Server or MySQL), as it does for ClickHouse.

The provider follows the ClickHouse provider's shape wherever it can: one row carries everything (no reference tables,
no surrogate ids), the collector writes the derived data, the provider has its own ingestion worker and its own read
repositories behind the unchanged Core interfaces. What is new is file management: layout, manifests, compaction.

## Status

Planned 2026-10-09. **Phase 0 (spikes) done 2026-10-10**: results in [phase-0-results.md](phase-0-results.md), verdicts applied below and in phases 1, 3, 4 and 5 (each change says "phase 0"). **Phase 1 implemented 2026-10-10** (uncommitted; see the implementation note in [phase-1-storage-and-span-writes.md](phase-1-storage-and-span-writes.md)). **Phase 2 implemented 2026-10-10** (uncommitted; note in its plan file). **Phase 3 (milestone 1, traces end to end) implemented 2026-10-10** (uncommitted; note in its plan file). **Phase 4 (compaction) implemented 2026-10-10** (uncommitted; note in its plan file). **Phase 5 (logs and metrics) implemented 2026-10-10** (uncommitted; note in its plan file). **Phase 6 (retention, stress harness, docs) implemented 2026-10-10** (uncommitted; note in its plan file). Phase 7 not started.

## User decisions (2026-10-09 interview)

| # | Decision |
|---|---|
| U1 | **DuckDB on both sides.** The collector writes files with DuckDB (appender into an in-memory table, then `COPY (SELECT ... ORDER BY ...) TO ... (FORMAT parquet)`); the API reads with DuckDB.NET through Dapper. One type mapping, and compaction reuses the writer. Cost accepted: DuckDB's native library in the collector |
| U2 | **A crash loses what is buffered.** Same semantics as today (an export is acknowledged before it is persisted), with a longer window (the linger). Shutdown drains. No write-ahead log in v1 |
| U3 | **Compaction runs on the API host**, one instance at a time under a lease, like `RollupCompactionWorker` |
| U4 | **Local or shared disk only** in v1. Object storage (S3/Azure through DuckDB's extensions) is a later phase behind a storage abstraction introduced now. *Phase 0 spike 3 narrowed "shared": the root must be on one machine's file system (a local disk, or a Docker volume or Linux-host bind mount shared by containers on that host); a network share between machines is unsupported in v1. Confirmed by the user 2026-10-10: one machine is enough for v1* |
| U5 | **Default linger about 30 s** (configurable): data is readable within about 30 s plus the size thresholds |
| U6 | **Per-partition manifests**, replaced by atomic rename, define which files a reader sees; a file replaced by compaction is deleted after a grace period |
| U7 | **Layout tenant / day / hour.** Compaction merges closed hours, then closed days. Retention is by day |
| U8 | **`Telemetry:Parquet:RootPath`** and the provider's other settings live in a `Telemetry:Parquet` section; no `ConnectionStrings:Collector`/`Api` for this provider |
| U9 | **More than one collector** can write the same root in v1, through per-writer manifests. *After phase 0: as several processes or containers on the root's machine (see U4)* |
| U10 | **The collector precomputes** the request and log rollups, `trace_index`, `metric_catalog` and `metric_series`, as Parquet |
| U11 | **`ExportMaxWindowDays` = 7** |
| U12 | **Milestone 1 is traces end to end** (phases 0 to 3) before logs and metrics |

## Plans, in order

| Phase | Plan | Items | Size |
|---|---|---|---|
| 0 | [phase-0-spikes.md](phase-0-spikes.md) | DuckDB.NET types, trace-id lookup, manifest read order, concurrency and cancellation, file-count read cost, packaging | Small to medium. Throwaway code, one results file |
| 1 | [phase-1-storage-and-span-writes.md](phase-1-storage-and-span-writes.md) | Project, options, file store, manifests, table definitions, span file writer | Large. The storage foundation |
| 2 | [phase-2-ingestion-worker.md](phase-2-ingestion-worker.md) | Hour buffers, linger, tokens as file names, retention drop, drain; shared with ClickHouse | Medium |
| 3 | [phase-3-trace-reads.md](phase-3-trace-reads.md) | `trace_index`, request rollup; DuckDB read engine; trace and rollup repositories; host wiring. **Milestone 1** | Large |
| 4 | [phase-4-compaction.md](phase-4-compaction.md) | Minor and major compaction, lease, grace deletes, manifest pruning | Medium to large |
| 5 | [phase-5-logs-and-metrics.md](phase-5-logs-and-metrics.md) | Log and point tables, log rollup, metric catalog and series; log, metric and resource repositories | Large |
| 6 | [phase-6-retention-harness-docs.md](phase-6-retention-harness-docs.md) | Day-folder retention, test infrastructure, stress harness, docs | Medium |
| 7 | [phase-7-measurement.md](phase-7-measurement.md) | Ramps and read probes; results recorded | Medium. Runs, not code |

Why this order:
- Phase 0 first: three things could change the design (whether DuckDB.NET can write the nested types, how a trace is
  found by id without an index, and whether the manifest read order is safe).
- Phases 1 to 3 build one signal through every layer, the hardest one (anchors, id lookup). After phase 3 the API host
  works on Parquet for the trace pages and the dashboard (the logs and metrics pages render empty until phase 5).
- Compaction (phase 4) before logs and metrics, so file-count problems show on one signal, and the compactor is
  generic over table definitions; phase 5's tables are compacted with no extra work.
- Phase 6 tidies and documents; phase 7 measures the finished whole.

## Decisions that apply to every phase

Confirmed by the user 2026-10-10 (with the phase 0 changes to D3 and D4 recorded in their rows).

| # | Decision |
|---|---|
| D1 | **Core interfaces, models and API responses do not change.** Where a Core read base holds provider-neutral C# the Parquet repositories need, it moves to a shared static helper, as ClickHouse's R4 did (much of that is already done: `MetricSeriesPipeline`, search parsing). The four existing providers' tests confirm nothing changed |
| D2 | **Timestamps are `BIGINT` Unix nanoseconds**, named `*_unix_nano` like the relational schema, not Parquet `TIMESTAMP`s. Exact, no driver conversion, and the shared rollup SQL (`RollupReadRepositoryBase`, which reads `bucket_start_unix_nano`) runs unchanged. Phase 0 confirms DuckDB prunes row groups on them |
| D3 | **Ids:** a trace id is **two `UBIGINT` columns, `trace_hi` and `trace_lo`** (the big-endian halves of its 16 bytes) in every table and nested struct that carries one; span ids are `UBIGINT`. Converted at the edges by one helper (`ParquetIds`), the ClickHouse R8 rule; queries match `trace_hi = ? AND trace_lo = ?`. Phase 0 spike 2: DuckDB does not prune on `UUID` statistics (a day's `trace_index` lookup took 2.5 s as `UUID`, 27 ms split) |
| D4 | **Attributes are `MAP(VARCHAR, VARCHAR)`**, values as strings (ClickHouse R7: `"500"`, not `500`); one converter (`ParquetAttributes`) for every table. Events, links, exemplars and quantiles are `LIST(STRUCT(...))`. **The appender cannot write `MAP` or `STRUCT`** (phase 0 spike 1), so the writer appends them as JSON text into a staging table and the `COPY`'s `SELECT` converts them with `json_transform` (250,000 spans/s on one connection) |
| D5 | **Reads see named tables through per-query views.** Before a statement runs, the repository resolves the file list for the request's tenant and time range from the manifests and defines `CREATE TEMP VIEW spans AS SELECT * FROM read_parquet([...])` (one per table the statement needs) on that connection. The SQL then reads `spans`, `request_rollup_minute` and so on, so shared SQL (the rollup base) runs as is and the provider's own SQL stays readable. Never a `**` glob |
| D6 | **A batch's file name is its deduplication token**: `part-<writer>-<seq>-<token>.parquet`. A retry of the same sealed buffer writes the same name and replaces it, so a retried flush is stored once (the Parquet form of `insert_deduplication_token`). A re-sent export is stored twice and reads tolerate it, like every other provider |
| D7 | **One table definition drives writer, compactor, retention and reads**: a `ParquetTable` record (name, columns with DuckDB types, sort key, partition time column, which retention window it follows). Adding a table is one definition |
| D8 | **Rollups keep the shared column names** (`bucket_start_unix_nano`, `request_count`, `h00`..`h23`, `record_count`, `severity_number` with `-1`), the ClickHouse R5 rule. Rows are partial; reads `SUM`; compaction pre-sums them |
| D9 | **`metric_catalog` and `metric_series` are partitioned by day** (one row per metric or series per day it was written, with first/last seen), so retention drops them with the day like everything else and nothing is rewritten in place. The catalog read aggregates the days in the metrics window. A metric id is the ClickHouse R6 hash of (tenant, service, name, type) |
| D10 | **The provider README is the operator guide**: the shared-disk requirement, sizing, what a crash loses, how to back up a root (copy the manifests last) |

## Layout

```
<RootPath>/
  _schema.json                                   telemetry schema version (4.0.0) and table definitions' hash
  _leases/compaction.lease                       compactor lease (phase 4)
  _writers/<writer-id>                           a collector's saved instance id (phase 1)
  <table>/tenant_id=<t>/date=<yyyy-MM-dd>/hour=<HH>/
      _manifest.<writer-id>.json                 one per collector that wrote here
      _compacted.<gen>.json                      the compactor's, one per generation, created by exclusive rename (phase 4)
      part-<writer>-<seq>-<token>.parquet        a collector's file
      c-<generation>-<n>.parquet                 a compacted file
  <table>/tenant_id=<t>/date=<yyyy-MM-dd>/_day/  a closed day's merged files and its `_compacted.<gen>.json` (phase 4)
```

A writer manifest lists its files with a per-writer, per-partition sequence number assigned when the file is committed.
A compacted manifest lists its files and, per writer, `through`: the highest sequence it has folded in. It is never
rewritten: each generation is a new `_compacted.<gen>.json`, published by rename without overwrite, so of two racing
compactors exactly one wins and the other discards its work. **A reader's file set is the newest compacted generation's
files plus each writer's files with a sequence above that writer's `through`.** Readers read the writer manifests *first*,
then list the partition again for the newest generation. Phase 0 spike 3 confirmed this order never loses, duplicates or
misses a file under concurrent commits and compactions (and that the opposite order does), on one machine's file system.

## Configuration (`Telemetry:Parquet`)

| Key | Default | Meaning |
|---|---|---|
| `RootPath` | (required) | The shared root; the collector and API must see the same files |
| `WriterId` | machine name + saved suffix | A collector's stable id (unique per instance on one root) |
| `LingerMilliseconds` / `LateLingerMilliseconds` | 30,000 / 120,000 | Open-hour and earlier-hour buffer linger |
| `MaxBufferRecords` / `MaxBufferBytes` | 500,000 / 256 MiB | Seal a buffer early |
| `RowGroupRows` | 122,880 | Parquet row group size (DuckDB's default; 30,720 measured no better without bloom filters). No bloom filters and the default `DICTIONARY_SIZE_LIMIT` (phase 0 spike 2: a raised limit ran a compaction out of memory and grew files 55%) |
| `Compression` | `zstd` | |
| `DuckDb:MemoryLimit` / `DuckDb:Threads` / `DuckDb:TempDirectory` | 2 GB / processor count / `<RootPath>/_tmp/<host>` | API host's read database (phase 0 spike 4: reads are CPU-bound; 2 GB was ample) |
| `Compaction:DuckDb:MemoryLimit` / `Threads` | 3 GB / 4 | The compactor's own database: an hour of 36 M spans sorts in about 140 s at a 4 GB peak (phase 0 spike 2) |
| `Writer:DuckDb:MemoryLimit` / `Threads` | 1 GB / 2 | The collector's writer database |
| `Compaction:*` | see [phase 4](phase-4-compaction.md) | `Enabled`, `IntervalSeconds` 60, `LeaseSeconds` 300, `MinorFileThreshold` 100, `MinorAgeSeconds` 600, `CloseHourGraceMinutes` 15, `CloseDayGraceHours` 6, `MaxFileRows` 10 M, `MaxSortRows` 20 M, `GraceSeconds` 600, `MaxConcurrentCompactions` 2, `ScanDays` 2, `FullScanMinutes` 60 |

## Out of scope

- A root on a network or virtualized share (NFS, SMB, Docker Desktop's host-to-VM bind mounts): spike 3 saw stale manifests through
  virtiofs, and none of the others were tested. So collectors and API hosts on different machines are out of scope for v1;
  supporting them is the object-storage phase's job (a manifest store with ordered reads), or a later spike on a specific share.
- Object storage (U4), a write-ahead log (U2), schema migration (fresh install only, as 4.0.0 everywhere).
- Any change to the relational providers or ClickHouse beyond the phase 2 worker extraction, which their tests guard.
- Querying the files from outside the API (they are plain Parquet, so it works, but the layout is not a public
  contract in v1).
