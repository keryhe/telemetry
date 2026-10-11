# Keryhe.Telemetry.Parquet

Parquet + DuckDB provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry): the collector writes telemetry
as Parquet files and the API reads them with an in-process DuckDB. No database server for telemetry. Set
`Database:Provider` to `Parquet`. Spans, logs and metrics work end to end, with compaction and day-granular retention
(design and decisions: `plans/parquet-duckdb/`; the ramp measurements of phase 7 are not recorded yet, so this README
states no throughput figures).

**Telemetry data only.** Like ClickHouse, a Parquet deployment runs the control plane (tenants, API keys, alert rules,
retention settings) on PostgreSQL, SQL Server or MySQL (`ControlPlane:Provider`).

## When to choose it

Choose Parquet when you want telemetry storage with **no database server to run**, on **one machine** (or several
containers on one Linux host sharing a volume), at modest volume. Choose PostgreSQL, SQL Server, MySQL or ClickHouse when
the collector and API hosts run on different machines, when you need a write-ahead guarantee (below), or when volume
outgrows one machine's disk and CPU. The control plane still needs PostgreSQL, SQL Server or MySQL.

**The root must be on one machine's file system.** A network share between machines (NFS, SMB) is unsupported: manifests
are read and swapped by atomic rename, and a stale directory listing breaks the read order. Docker Desktop bind mounts
from the host into its VM also showed stale reads. Use a local disk or a Docker volume.

## Usage

```csharp
// Collector host
builder.Services.AddParquetCollectorServices(builder.Configuration);
builder.Services.AddPostgreSqlControlPlaneCollectorServices(builder.Configuration); // or SqlServer / MySql

// API host (the same RootPath)
builder.Services.AddParquetApiServices(builder.Configuration);
builder.Services.AddPostgreSqlControlPlaneApiServices(builder.Configuration);
```

Set `Database:Provider` to `Parquet` and `Telemetry:Parquet:RootPath` on both hosts (there is no `ConnectionStrings:Collector`
or `:Api` for this provider); `ControlPlane:Provider` and `ConnectionStrings:ControlPlane` as for ClickHouse. There is no
schema script: `schema/apply-schema.sh telemetry parquet <root>` only creates the directory, and the collector writes
`_schema.json` on first start. Start the collector before the API host (the API refuses a root that does not exist).

## What it provides

- `AddParquetCollectorServices(configuration)`: `ITelemetryBulkWriter` that writes one Parquet file per tenant-hour
  partition and table (spans, logs, the five metric point tables and the derived tables) and lists it in the collector's
  manifest; checks the root at startup.
- `AddParquetApiServices(configuration)`: the options and file store, the read-side root check, the DuckDB read engine, the
  trace, rollup and retention services, and the **compaction worker**.

## Configuration (`Telemetry:Parquet`)

| Key | Default | Meaning |
|---|---|---|
| `RootPath` | (required) | The data directory. The collector and the API must see the same files **on one machine's file system** (a local disk, or a volume shared by containers on one Linux host; not a network share) |
| `WriterId` | machine name + saved suffix | A collector's stable id. Two live collectors must not share one: the second fails at startup |
| `LingerMilliseconds` / `LateLingerMilliseconds` | 30,000 / 120,000 | Buffer linger (used from phase 2) |
| `MaxBufferRecords` / `MaxBufferBytes` | 500,000 / 256 MiB | Seal a buffer early (phase 2) |
| `RowGroupRows` | 122,880 | Parquet row group size |
| `Compression` | `zstd` | `zstd`, `snappy`, `gzip` or `uncompressed` |
| `CatalogRefreshSeconds` / `MaxTrackedSeries` | 300 / 1,000,000 | A metric or series that keeps reporting has its `metric_catalog`/`metric_series` row re-written at most this often; the writer forgets past the limit |
| `DuckDb:MemoryLimit` / `Threads` / `TempDirectory` | 2 GB / processor count / `<RootPath>/_tmp/<host>` | The API host's read database |
| `Writer:DuckDb:MemoryLimit` / `Threads` | 1 GB / 2 | The collector's file-writing database |
| `Compaction:Enabled` | true | Runs on the API host only; one instance at a time holds `_leases/compaction.lease` |
| `Compaction:IntervalSeconds` / `LeaseSeconds` | 60 / 300 | The worker wakes and renews its lease every interval; another instance takes over a lease unrenewed for `LeaseSeconds` |
| `Compaction:MinorFileThreshold` / `MinorAgeSeconds` | 100 / 600 | A partition with more uncompacted files than this, or two or more older than this, is folded into one new file (an hour of ~100 files reads as fast as one) |
| `Compaction:CloseHourGraceMinutes` / `CloseDayGraceHours` | 15 / 6 | An hour over this long is rewritten into one file set; a day over this long is merged into `_day/` |
| `Compaction:MaxFileRows` / `MaxSortRows` | 10 M / 20 M | A compacted file is split at this many rows; a bigger sort is cut on the first sort column |
| `Compaction:GraceSeconds` | 600 | A superseded file is deleted this long after it was superseded (see below) |
| `Compaction:MaxConcurrentCompactions` | 2 | Tenant-days compacted at a time |
| `Compaction:ScanDays` / `FullScanMinutes` | 2 / 60 | A pass looks at the last `ScanDays` UTC days; every `FullScanMinutes` it looks at all of them |
| `Compaction:DuckDb:MemoryLimit` / `Threads` | 3 GB / 4 | The compactor's own database (dictionaries are not spilled, so memory bounds a sort) |

## Layout

```
<RootPath>/_schema.json                 schema version and a hash of the table definitions
<RootPath>/_writers/<writer-id>         a collector's saved id (held open while it runs)
<RootPath>/<table>/tenant_id=<t>/date=<yyyy-MM-dd>/hour=<HH>/
    _manifest.<writer-id>.json          the files that collector committed here
    part-<writer-id>-<seq>-<token>.parquet
    _compacted.<generation>.json        the compactor's file list and per-collector `through`
    c-<generation>-<compactor>-<n>.parquet
<RootPath>/<table>/tenant_id=<t>/date=<yyyy-MM-dd>/_day/   a merged day: its generations and files
<RootPath>/_leases/compaction.lease     the compactor's lease
```

A file is visible only once its manifest lists it. A crash loses what the collector had buffered and not yet written.

## Compaction

Every linger of every collector adds a file per tenant-hour per table, so the API host's `ParquetCompactionWorker` folds them.
It holds a lease (`_leases/compaction.lease`), so with several API instances one compacts; it needs no other coordination:
a compacted file set is published as the next `_compacted.<generation>.json` by rename *without overwrite*, so of two compactors
that overlap one wins and the other deletes its work.

- **Minor** (any hour, the open one too): uncompacted writer files, when over `MinorFileThreshold` or older than `MinorAgeSeconds`,
  fold into one new sorted file; the generation records, per collector, the highest file sequence folded (`through`).
- **Major**: an hour over for `CloseHourGraceMinutes` is rewritten into one file set. **Day**: a day over for `CloseDayGraceHours` is
  merged into `date=.../_day/`; its generation is the one switch that moves all the day's hours there. A file that arrives
  later (a late retry) is read at once and merged by the next day pass.
- The **rollup tables are pre-summed** (one row per key; reads still `SUM`), `trace_index` per trace, service and hour.
- A reader's file set is the newest generation's files plus each collector's files above its `through`; readers list the
  collectors' manifests first, then the generation (the opposite order loses files under concurrent compaction).
- **Deleting.** A superseded file is deleted `GraceSeconds` after it was superseded. **An export or query that runs longer
  than the grace and reads a file the compactor then deletes fails with an error**; exports are unbounded, so raise
  `GraceSeconds` above your longest one. Files no manifest lists and dead temp files go once they are over an hour old.
- Metrics on the `Keryhe.Telemetry.Parquet` meter: `compaction_duration` (by kind), `files_compacted`, `files_deleted`,
  `compaction_failures`, `uncompacted_files` (the largest count in one partition at the last pass). A partition that fails is
  retried with back-off and never blocks the others.
Schema 4.0.0 is a fresh-install schema: a root written under other table definitions is refused.

## Operating it

- **A crash loses what is buffered.** An export is acknowledged once it is queued, as on every provider, but a collector holds
  records for `LingerMilliseconds` (30 s) before writing a file, so a crash loses up to about that much plus whatever is queued.
  A graceful shutdown drains. There is no write-ahead log.
- **Data is readable after the linger**: about 30 s (`LingerMilliseconds`) after it arrives, sooner for a buffer that reaches
  `MaxBufferRecords`.
- **Sizing the queue.** The ingestion gates (`Telemetry:Ingestion:MaxQueuedSpans`, `MaxQueuedLogRecords`, `MaxQueuedMetrics`)
  count buffered records too, so each must be at least `MaxBufferRecords`; the collector warns at startup when one is lower.
  Records held for 30 s at your rate must fit: 10,000 spans/s needs about 300,000 resident spans, plus headroom. Unless you set them,
  the Parquet registration defaults each record gate to twice `MaxBufferRecords` (1,000,000) and `MaxQueuedBytesPerSignal` to 2 GB
  (the shared defaults, 200,000 and 256 MB, capped ingest near 18,000 records/s; with these about 64,000 write-only on the reference machine).
- **Retention** is by day: a tenant's `date=` folder is deleted (for each table the signal's window covers, derived tables
  with it) once the whole day is before the cutoff, so the cutoff's own day survives until it ends. The sweep takes its tenants
  from the folders, so data of a tenant removed from the control plane still expires. A day is deleted at once (not after the
  compaction grace), so a query or export that reaches back past the retention window can fail on a file that disappears;
  anything inside the window is unaffected. The collector drops records already past their window at ingest.
- **Backup.** Copy the data files first and the manifests (`_manifest.*.json`, `_compacted.*.json`) last, so no manifest names
  a file the copy lacks; or stop the collector and compactor and copy the root. A file no manifest names is ignored by readers.
- **Search and attributes behave as on ClickHouse**: attribute values are text (`"500"`, not `500`), a metric "instance" is
  one service's metric rather than one resource, and the derived tables (rollups, `trace_index`, `metric_catalog`,
  `metric_series`) are written by the collector, so a failed derived write under-counts the cards, charts and catalog.
- **Diagnosing slow reads.** Set the log level of `Keryhe.Telemetry.Parquet.ParquetQueryEngine` to `Debug` on the API host: each
  statement then logs `parquet-sql ms=<elapsed> files=<files its views read> sql=<text>`. The stress harness reads these.
  A large `files` count means the compactor is behind (`uncompacted_files` on the meter).

## Size

The package depends on `DuckDB.NET.Data.Full`, whose native library is 35-112 MB per platform and 315 MB in a publish
without a runtime identifier (all five platforms). Publish with `-r <rid>` (for example `-r linux-x64`) to carry one.
Alpine/musl is not supported.
