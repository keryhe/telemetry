# Keryhe.Telemetry.Parquet

Parquet + DuckDB telemetry provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry): the collector writes Parquet
files and the API reads them with an in-process DuckDB, so telemetry needs no database server. Both hosts must run on **one
machine** and see the same root on a local file system (not a network share or a Docker Desktop bind mount). The control
plane runs on PostgreSQL, SQL Server, MySQL or a SQLite file (`Keryhe.Telemetry.Sqlite`).

## Usage

```csharp
// Collector host
builder.Services.AddParquetCollectorServices(builder.Configuration);
builder.Services.AddSqliteApiKeyLookup(builder.Configuration);              // or AddPostgreSqlControlPlaneCollectorServices, ...

// API host (the same RootPath)
builder.Services.AddParquetApiServices(builder.Configuration);
builder.Services.AddSqliteControlPlaneManagement(builder.Configuration);    // or AddPostgreSqlControlPlaneApiServices, ...
```

Set `Provider:Database=Parquet` (under `Telemetry:Collector` and `Telemetry:Api`) and `Telemetry:Parquet:RootPath` on both hosts (no `ConnectionStrings:Collector`/`:Api`), plus
`Provider:ControlPlane` and `ConnectionStrings:ControlPlane`. Either host creates the root and its
`_schema.json` when they are missing (as both create a SQLite control plane), so the hosts start in either order. Publish with `-r <rid>` so only one platform's DuckDB
library ships.

## Configuration (`Telemetry:Parquet`)

| Key | Default | Meaning |
|---|---|---|
| `RootPath` | (required) | The data directory. The collector and the API must see the same files **on one machine's file system** (a local disk, or a volume shared by containers on one Linux host; not a network share) |
| `WriterId` | machine name + saved suffix | A collector's stable id. Two live collectors must not share one: the second fails at startup |
| `LingerMilliseconds` / `LateLingerMilliseconds` | 5,000 / 120,000 | How long the open hour's buffer collects records before it is written as a file; the same for an earlier hour's (late data) |
| `MaxBufferRecords` / `MaxBufferBytes` | 50,000 / 256 MiB | A buffer this large is written early |
| `RowGroupRows` | 122,880 | Parquet row group size |
| `Compression` | `zstd` | `zstd`, `snappy`, `gzip` or `uncompressed` |
| `CatalogRefreshSeconds` / `MaxTrackedSeries` | 300 / 1,000,000 | A metric or series that keeps reporting has its `metric_catalog`/`metric_series` row re-written at most this often; the writer forgets past the limit |
| `DuckDb:MemoryLimit` / `Threads` / `TempDirectory` | 2 GB / processor count / `<RootPath>/_tmp/<host>` | The API host's read database |
| `Writer:DuckDb:MemoryLimit` / `Threads` | 1 GB / 2 | The collector's file-writing database |
| `Compaction:Enabled` | true | Runs on the API host only; one instance at a time holds `_leases/compaction.lease` |
| `Compaction:IntervalSeconds` / `LeaseSeconds` | 15 / 300 | The worker wakes and renews its lease every interval; another instance takes over a lease unrenewed for `LeaseSeconds` |
| `Compaction:MinorFileThreshold` / `MinorAgeSeconds` | 30 / 60 | A partition with more uncompacted files than this, or two or more older than this, is folded into one new file. |
| `Compaction:CloseHourGraceMinutes` / `CloseDayGraceHours` | 15 / 6 | An hour over this long is rewritten into one file set; a day over this long is merged into `_day/` |
| `Compaction:MaxFileRows` / `MaxSortRows` | 10 M / 20 M | A compacted file is split at this many rows; a bigger sort is cut on the first sort column |
| `Compaction:GraceSeconds` | 600 | A superseded file is deleted this long after it was superseded (an export or query running longer may fail) |
| `Compaction:MaxConcurrentCompactions` | 2 | Tenant-days compacted at a time |
| `Compaction:ScanDays` / `FullScanMinutes` | 2 / 60 | A pass looks at the last `ScanDays` UTC days; every `FullScanMinutes` it looks at all of them |
| `Compaction:DuckDb:MemoryLimit` / `Threads` | 3 GB / 4 | The compactor's own database (dictionaries are not spilled, so memory bounds a sort) |

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)