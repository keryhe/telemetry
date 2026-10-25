# Parquet provider

How the Parquet + DuckDB provider ([`Keryhe.Telemetry.Parquet`](../src/Keryhe.Telemetry.Parquet/README.md)) stores, compacts and expires telemetry, and how to operate it. Its keys are in the package README and [CONFIGURATION.md](CONFIGURATION.md#parquet-telemetryparquet).

## When to choose it

Choose Parquet when you want telemetry storage with **no database server to run**, on **one machine** (or several
containers on one Linux host sharing a volume), at modest volume. Choose PostgreSQL, SQL Server, MySQL or ClickHouse when
the collector and API hosts run on different machines, when you need a write-ahead guarantee (below), or when volume
outgrows one machine's disk and CPU. The control plane runs on PostgreSQL, SQL Server, MySQL or a SQLite file.

**The root must be on one machine's file system.** A network share between machines (NFS, SMB) is unsupported: manifests
are read and swapped by atomic rename, and a stale directory listing breaks the read order. Docker Desktop bind mounts
from the host into its VM are unsupported for the same reason. Use a local disk or a Docker volume.

## What it provides

- `AddParquetCollectorServices(configuration)`: `ITelemetryBulkWriter` that writes one Parquet file per tenant-hour
  partition and table (spans, logs, the five metric point tables and the derived tables) and lists it in the collector's
  manifest; checks the root at startup.
- `AddParquetApiServices(configuration)`: the options and file store, the read-side root check, the DuckDB read engine, the
  trace, rollup and retention services, and the **compaction worker**.

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

The root's `_schema.json` records the table definitions it was written with; a root written under other definitions is refused.

## Operating it

- **A crash loses what is buffered.** An export is acknowledged once it is queued, as on every provider, but a collector holds
  records for `LingerMilliseconds` (5 s) before writing a file, so a crash loses up to about that much plus whatever is queued.
  A graceful shutdown drains. There is no write-ahead log.
- **Data is readable after the linger**: about 5 s (`LingerMilliseconds`) after it arrives, sooner for a buffer that reaches
  `MaxBufferRecords`.
- **Sizing the queue.** The ingestion gates (`Telemetry:Ingestion:MaxQueuedSpans`, `MaxQueuedLogRecords`, `MaxQueuedMetrics`)
  count buffered records too, so each must be at least `MaxBufferRecords`; the collector warns at startup when one is lower.
  Records held for one linger at your rate must fit: at the 5 s default, 10,000 spans/s needs about 50,000 resident spans, plus headroom. Unless you set them,
  the Parquet registration defaults each record gate to 1,000,000 (or twice `MaxBufferRecords` if that is more) and `MaxQueuedBytesPerSignal` to 2 GB
  (the shared defaults, 200,000 and 256 MB, cap ingest near 200,000 / linger per signal).
  A shorter linger writes more, smaller files, which the compactor folds; raise `LingerMilliseconds` and `MaxBufferRecords` to write fewer files at the cost of freshness and memory.
- **Retention** is by day: a tenant's `date=` folder is deleted (for each table the signal's window covers, derived tables
  with it) once the whole day is before the cutoff of that tenant's own window (the built-in 30 days for a tenant with no control-plane row), so the cutoff's own day survives until it ends. The sweep takes its tenants
  from the folders, so data of a tenant removed from the control plane still expires. A day is deleted at once (not after the
  compaction grace), so a query or export that reaches back past the retention window can fail on a file that disappears;
  anything inside the window is unaffected. The collector drops records already past their tenant's window at ingest (after a window is lengthened, records between the old and new cutoff are still dropped for up to the refresh interval).
- **Backup.** Copy the data files first and the manifests (`_manifest.*.json`, `_compacted.*.json`) last, so no manifest names
  a file the copy lacks; or stop the collector and compactor and copy the root. A file no manifest names is ignored by readers.
- **Search and attributes behave as on ClickHouse**: attribute values are text (`"500"`, not `500`), a metric "instance" is
  one service's metric rather than one resource, and the derived tables (rollups, `trace_index`, `metric_catalog`,
  `metric_series`) are written by the collector, so a failed derived write under-counts the cards, charts and catalog.
- **Diagnosing slow reads.** Set the log level of `Keryhe.Telemetry.Parquet.ParquetQueryEngine` to `Debug` on the API host: each
  statement then logs `parquet-sql ms=<elapsed> files=<files its views read> sql=<text>`.
  A large `files` count means the compactor is behind (`uncompacted_files` on the meter).

## Size

The package depends on `DuckDB.NET.Data.Full`, whose native library is 35-112 MB per platform and 315 MB in a publish
without a runtime identifier (all five platforms). Publish with `-r <rid>` (for example `-r linux-x64`) to carry one.
Alpine/musl is not supported.
