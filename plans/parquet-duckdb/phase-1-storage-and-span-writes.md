# Phase 1: storage foundation and span writes

Part of the [Parquet + DuckDB provider](README.md). Creates `Keryhe.Telemetry.Parquet` with everything below the
ingestion worker: options, the file store, manifests, table definitions and the span file writer. Batching is phase 2;
`trace_index` and the request rollup are phase 3.

## Decisions

| # | Decision |
|---|---|
| 1 | New project `src/Keryhe.Telemetry.Parquet` (in `Telemetry.sln`, packable like the other providers), referencing Core and `DuckDB.NET.Data.Full`. No control plane: like ClickHouse, a Parquet deployment sets `ControlPlane:Provider` to a relational provider |
| 2 | **`IParquetFileStore`** is the only code that touches the filesystem: list a partition's manifests, read/write a manifest atomically (temp + `File.Move(overwrite: true)` for a writer's own manifest; temp + `File.Move(overwrite: false)`, an exclusive create, for a compacted generation), commit a file (temp + rename), delete, enumerate partitions by tenant and date range, delete a day folder. One implementation, `LocalParquetFileStore`, now; object storage later implements the same interface (U4) |
| 3 | **Writes go to `<partition>/.tmp/<name>`** and are renamed into the partition, so a reader never sees a partial file, and a crash leaves only `.tmp` files, which a writer deletes from its own id on start |
| 4 | **`ParquetTable` definitions** (README D7) live in one class, `ParquetTables`, with the span table first: columns, DuckDB types, sort key `(service_name, start_time_unix_nano)`, partition time `start_time_unix_nano`, retention window `traces`. Phase 0 spike 2 may change the sort key |
| 5 | **`_schema.json`** at the root holds the telemetry schema version (4.0.0) and a hash of the table definitions. The collector creates it on first start if the root is empty; both hosts refuse to start on a different version or hash, naming both. There are no SQL scripts; `apply-schema.sh telemetry parquet <root>` only creates the root and `_schema.json` (for symmetry with the other providers and the docs) |
| 6 | **Writer id**: `Telemetry:Parquet:WriterId`, else the machine name plus a random suffix saved in `_writers/` on first start, so a restarted collector keeps its id and its manifests. Two live collectors with one id is a misconfiguration; the collector takes an exclusive lock on its `_writers/<id>` file and fails startup if it cannot |
| 7 | **Sequence numbers** are per writer per partition, assigned under the writer's manifest lock when a file is committed, so a writer manifest's files are in commit order. The manifest records each file's sequence, row count, byte size and min/max of the partition time column (the compactor and readers use the counts and bounds without opening files) |
| 8 | The span row builder copies the resolved resource and scope onto every span (`ResourceScopeCache` is not used), mirroring `ClickHouseBulkWriter`'s span rows; `duration_ns = max(0, end - start)`; absent `service.name` is `''` |
| 9 | `ParquetBulkWriter` implements `ITelemetryBulkWriter` (the shared test bases seed through it) with token overloads behind an internal seam (`IParquetTokenWriter`), the ClickHouse R2 shape: the interface methods mint a fresh token per call. A flush groups its rows by (tenant, date, hour) and writes one file per partition, each with a token derived from the batch token and the partition. A flush is complete when every file is committed and every manifest updated; a failure leaves already-committed partitions in place, and the retry rewrites them under the same names (D6) |
| 10 | Logs and metrics `Flush*Async` throw `NotSupportedException` until phase 5 |
| 11 | **Staging (phase 0 spike 1).** The appender writes scalars natively and maps and nested lists as JSON text (`System.Text.Json`) into a temp table; the `COPY`'s `SELECT` converts them with `json_transform(col, '"MAP(VARCHAR, VARCHAR)"')` and a struct-list structure cast to the column type. The trace id is appended as its two halves (README D3) |
| 12 | **Packaging (phase 0 spike 6; user decision 2026-10-10).** `Keryhe.Telemetry.Parquet` depends on `DuckDB.NET.Data.Full`, so a publish without a runtime identifier carries DuckDB's native library for five platforms (315 MB; 35-112 MB with `-r <rid>`). Accepted: the in-repo hosts keep referencing every provider (they are development examples), and a consumer builds their own host from the NuGet packages with only the provider they use. The provider README notes the size and that `-r <rid>` trims it. Alpine/musl is unsupported (no musl build) |

## Steps

1. Project, `ParquetOptions` (README's configuration table, `Validate()`), `AddParquetCollectorServices` and
   `AddParquetApiServices` with the options and the file store only.
2. `ParquetIds`, `ParquetAttributes` (from phase 0's verdicts), `ParquetTables`.
3. `LocalParquetFileStore`, `ParquetManifest` (JSON model, versioned) and `ManifestWriter` (per-writer, per-partition
   lock; commit = rename file, then rewrite manifest).
4. `ParquetFileWriter`: one in-memory DuckDB database per collector process, a connection per write; appender into a
   temp table per table definition, then `COPY (SELECT * FROM t ORDER BY <sort key>) TO <tmp path> (...)`.
5. `ParquetBulkWriter` for spans.
6. Startup checks: root exists and is writable (collector) or readable (API), `_schema.json`, writer-id lock.

## Tests

New `tests/Keryhe.Telemetry.Parquet.Tests` (no Docker, temp directories):
- Every span column round-trips through a flush and a DuckDB read (ids with the high bit set, nanoseconds exact,
  attribute encodings, events and links).
- A flush across two hours and two tenants writes four partitions with one file each, correct manifests and counts.
- Retrying a flush with the same token leaves one file per partition with the same rows; a new token adds files.
- A file being written is not listed; a crash between file rename and manifest rewrite leaves an unlisted file that
  is not read (and is cleaned up by compaction's orphan sweep in phase 4).
- `_schema.json` mismatch and a duplicate writer id fail startup with the message naming the problem.

## Done when

`ParquetBulkWriter.FlushTracesAsync` writes correct, listed span files under the layout, and the tests pass.

## Implementation note (2026-10-10, uncommitted)

Done: `src/Keryhe.Telemetry.Parquet` and `tests/Keryhe.Telemetry.Parquet.Tests` (22 tests, no Docker, all pass), both in
`Telemetry.sln`; `apply-schema.sh telemetry parquet <root>`. Where the code differs from or settles the plan:

- **Hosts not wired.** `AddParquetCollectorServices`/`AddParquetApiServices` exist but `Collector.Server` and `Api.Server` do
  not switch on `Database:Provider = "Parquet"` yet (phase 3 wires the API; with the shared worker still in place, a
  Parquet collector would also hit the not-yet-supported logs and metrics flushes, so phase 2 wires the collector).
- **`json_transform` takes the column's DuckDB type string directly** (`'"STRUCT(...)[]"'`), so `ParquetColumn` needs no
  separate JSON structure per nested column; events and links round-trip (tested).
- **Retry = same file name.** The manifest looks up the token: a commit under a token it already holds replaces that file
  and keeps its sequence (README D6); a new token takes `NextSequence`. `NextSequence` is persisted in the manifest.
- **Writer ids** allow letters, digits, `_` and `-` only (a `.` would let one id prefix another in the temp-file cleanup).
  Without a configured id a restart reuses the first unlocked saved `_writers/<machine>-*` id; a second live collector on
  the machine gets its own.
- **Columns:** `kind` and `status_code` are `VARCHAR` (as on ClickHouse); `end_time_unix_nano` is stored too; span and link
  ids are big-endian `UBIGINT` (hex-parse), trace ids `trace_hi`/`trace_lo`.
- **`_schema.json`** is created by the collector, not by `apply-schema.sh` (the shell cannot compute the table-definition
  hash); the script only creates the root.
- **Not done here:** `ParquetManifests.ListFilesAsync` reads writer manifests only (compacted generations: phase 4; the
  reader's file-set resolution: phase 3). Partition files are written one after another, not in parallel (phase 2 may).
