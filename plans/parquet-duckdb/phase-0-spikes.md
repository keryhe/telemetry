# Phase 0: spikes

**Status: done 2026-10-10; results in [phase-0-results.md](phase-0-results.md), verdicts applied to the README and phases 1, 3, 4 and 5.**

Part of the [Parquet + DuckDB provider](README.md). Six spikes, run before any production code, each naming the
decisions it can overturn and what a failure triggers.

## Decisions

| # | Decision |
|---|---|
| 1 | Spikes are throwaway: a console project under `spikes/parquet-duckdb/` (not in `Telemetry.sln`). Nothing is merged into `src/` as is |
| 2 | They use the newest DuckDB.NET (`DuckDB.NET.Data.Full`) release that is at least two weeks old, on macOS arm64 and linux-x64 (the container image); record the version |
| 3 | Data is shaped like the test data generator's: attribute counts and sizes from a sample of today's spans, logs and points |
| 4 | Results go in `plans/parquet-duckdb/phase-0-results.md`, one section per spike: what ran, the numbers, a verdict (holds, adjust, fails) |
| 5 | A failed spike stops the plan until the README's affected decision and the phases built on it are rewritten |

## Spikes

### 1. Writing every column type

Through DuckDB.NET's appender into an in-memory table, then `COPY (SELECT ... ORDER BY ...) TO 'x.parquet' (FORMAT
parquet, COMPRESSION zstd, ROW_GROUP_SIZE n)`:
- `UUID`, `UBIGINT` (values with the high bit set), `BIGINT` nanoseconds;
- `MAP(VARCHAR, VARCHAR)`, including an empty map;
- `LIST(STRUCT(...))` for events, links and exemplars, including a `MAP` inside the struct;
- `VARCHAR` with non-ASCII text, `DOUBLE`, `UTINYINT`, nullable columns.

Read every column back through Dapper (and confirm what .NET type each arrives as). Time 500,000 spans: appender
rate, `COPY` time, file size per span.

**Can overturn:** D3, D4. **If the appender cannot write a type:** try `INSERT ... SELECT` from a prepared statement or
`read_json` over an in-memory buffer; if none is fast enough, the column becomes JSON text (D4's fallback) or hex.

### 2. Finding a trace by id

Trace detail and logs-by-trace have no index. Over an hour of spans at 10,000 spans/s (36 million rows, files sorted by
`(service_name, start_time_unix_nano)`), time `WHERE trace_id = ?` for:
- (a) Parquet bloom filters on `trace_id` (DuckDB's `BLOOM_FILTER_FALSE_POSITIVE_RATIO`; check they are written for a
  high-cardinality column, which may need `DICTIONARY_SIZE_LIMIT` raised, and that DuckDB uses them on read);
- (b) compacted files sorted by `(service_name, trace_id)` within the hour, so min/max stats prune on `trace_id`
  (and the cost of that sort to the service/time list reads);
- (c) `trace_index` carrying the file and row group of each trace, rewritten by compaction.

Each with and without the `?start=&end=` hint narrowing the hours.

**Can overturn:** phase 3's detail read, phase 4's sort keys. **Target:** detail of a hinted trace under 200 ms, an
unhinted one under 1 s over 24 h. **Pick** the simplest that meets it, in the order (a), (b), (c).

### 3. Manifest read order

A test harness with two writer threads committing files (write temp, rename file, rewrite manifest by temp + rename)
and a compactor thread folding them (write compacted file, rename compacted manifest, then later delete sources), and
reader threads resolving file sets (writer manifests first, then compacted) and counting rows. Run for an hour of
wall time on local APFS/ext4 and on a Docker bind mount, and on an NFS or SMB share if one is available.

**Can overturn:** the README's layout rules, U6. **Pass:** every read's row count equals the committed rows at some
moment between its start and end; no read hits a missing file inside the grace period. **If it fails:** a single
manifest per partition with a lock file (and a writer's own manifest only for files not yet published).

### 4. Concurrency, memory and cancellation in the API process

One `DuckDBConnection` database instance shared by the process (`DataSource=:memory:`), a connection per query.
Run 32 concurrent reads (a mix of list, summary, detail) over a day of data with `memory_limit` at 2 GB and 8 GB.
Record p50/p95, peak process memory, and whether out-of-memory surfaces as an exception or kills the process. Then
cancel a long scan: confirm `DbCommand.Cancel()` or a `CancellationToken` interrupts it within a second, and which
exception arrives (phase 3's `TimedQuery` rule: the deadline decides, not the exception type).

**Can overturn:** README configuration defaults, phase 3 decision 4. **If cancellation does not work:** run each
timed query on its own connection and call `duckdb_interrupt` through the native API (or close the connection).

### 5. Reading many small files

The current hour before compaction: 2 collectors x 120 files per signal per tenant. Time a trace-list first page and
a 1-hour summary over 1, 10, 120 and 1,000 files holding the same rows, with an explicit file list (D5). This sets the
minor compaction threshold in phase 4.

**Can overturn:** U5's default linger, phase 4's thresholds.

### 6. Packaging

`dotnet publish` the collector and API hosts for linux-x64, linux-arm64, win-x64 and osx-arm64 with the DuckDB package;
record the size added and that the native library loads in the `mcr.microsoft.com/dotnet/aspnet` image. Check the
license (MIT) and DuckDB.NET's support for .NET 10.

**Can overturn:** U1 (the fallback is Parquet.Net for the collector's writes, with DuckDB only in the API).

## Done when

`phase-0-results.md` has a verdict for all six, and the README and phases 1 to 4 are amended where a verdict says so.
