# Phase 0 results

Part of the [Parquet + DuckDB provider](README.md); the spikes are in [phase-0-spikes.md](phase-0-spikes.md) and the
code in `spikes/parquet-duckdb/` (throwaway, not in `Telemetry.sln`; `dotnet run -c Release -- <spike>` with `types`, `spanwrite`,
`lookup`, `lookup2`, `manifest`, `concurrency`, `files`, or `sql "<query>"` for ad hoc probes; `run-volume-manifest.sh` runs spike 3
across two containers). Its `out/` (about 42 GB of generated data, gitignored) was deleted on 2026-10-10; `lookup` regenerates it in about
15 minutes, and spike 4 needs `lookup2`'s output too.

**Setup.** DuckDB.NET.Data.Full **1.5.5** (DuckDB v1.5.5; 1.5.6, released 2026-09-28, was under two weeks old), .NET 10.0.201,
Apple M1 (8 cores, 16 GB), local APFS SSD, unless a section says otherwise. Synthetic spans shaped like the test data
generator's (`SpanData.cs`): 16 resource attributes (Topology/ResourceFactory.cs's set), 8 span attributes, about 10% of
spans with an exception event of 3 attributes, 5% with a link, traces of 10 spans over 8 services. The synthetic data
compresses unusually well (44 bytes per span in Parquet) because its values repeat more than real traffic's; sizes below
are therefore lower bounds and the write and read times optimistic in proportion.

## Summary

| # | Spike | Verdict | Plan changes |
|---|---|---|---|
| 1 | Writing every column type | **Adjust** | The appender cannot write `MAP` or `STRUCT`; append JSON text and convert with `json_transform` in the `COPY` (250,000 spans/s on one connection). D4 holds for the file; the staging path changes |
| 2 | Finding a trace by id | **Adjust** | DuckDB does not prune on `UUID` statistics: the trace id becomes two `UBIGINT` columns (`trace_hi`, `trace_lo`). No bloom filters (+55% size for 15%). Detail = `trace_index` (27 ms for a day) + a scan of the trace's hours (about 150 ms per hour of 36 M spans): option (a) without bloom. Compaction must bound DuckDB's memory (see "Compaction cost") |
| 3 | Manifest read order | **Holds on one machine's file system; fails across a virtualized share** | Correct on local APFS (20 min) and on a Docker volume shared by two containers (10 min); readers inside Docker Desktop's VM reading a macOS bind mount saw stale manifests (regressions, gaps). v1 supports a root on one machine's file system only. Compacted manifests become exclusive-create generation files (`_compacted.<gen>.json`), which made two racing compactors harmless |
| 4 | Concurrency, memory, cancellation | **Holds** | One shared in-process database works; cancellation (`Cancel()`, a token, Dapper's `CommandDefinition` token) ends a scan in about 1 s with `OperationCanceledException` and the connection stays usable; out of memory is a `DuckDBException`, not a crash. Reads are CPU-bound: the engine's `threads` is the knob, not memory |
| 5 | Many small files | **Adjust** | Up to about 120 files per partition read as fast as one; 1,000 are 3-5x slower. Minor compaction threshold about 100 files, not 16 |
| 6 | Packaging | **Holds, with a packaging decision** | MIT; loads on linux-arm64 (glibc) and macOS; +35 to +112 MB per platform, but **+315 MB** in a publish without a runtime identifier, which every host would carry since the hosts reference every provider |

## 1. Writing every column type

**Ran.** `types`: one value of each type through `DuckDBAppender`, `COPY ... TO parquet`, read back through the ADO.NET reader.
`spanwrite`: 500,000 spans through three staging forms, timed (second run of each, after a warm-up), and the files compared.

| Type | Appender | Read back as |
|---|---|---|
| `UUID` | OK (`Guid`) | `Guid`, exact |
| `UBIGINT`, high bit set | OK | `UInt64`, exact |
| `BIGINT` nanoseconds | OK | `Int64`, exact |
| `VARCHAR` non-ASCII, `DOUBLE`, `UTINYINT`, NULL | OK | as written |
| `VARCHAR[]` | OK (`List<string>`) | `List<string>` |
| `MAP(VARCHAR, VARCHAR)` | **`NotImplementedException`: "Writing Map to data chunk is not yet supported"** | (via SQL) `Dictionary<string, string>` |
| `STRUCT(...)[]` | **`NotImplementedException`: "Writing Struct to data chunk is not yet supported"** | (via SQL) `List<Dictionary<string, object>>` |

Staging forms for the span table (attributes and resource attributes as `MAP`, events and links as `LIST(STRUCT)` with a
`MAP` inside), 500,000 spans, one connection:

| Form | Append | `COPY` | Spans/s | File |
|---|---|---|---|---|
| **Lists**: maps as parallel `VARCHAR[]`, nested lists as parallel lists, rebuilt with `MAP(k, v)` and `list_transform(... struct_pack(...))`; resources once in a side table, joined | 2,091 ms | 2,096 ms | 119,000 | 21.0 MiB |
| **Json**: JSON text, converted with `json_transform(col, '"MAP(VARCHAR, VARCHAR)"')` and a struct-list structure | 1,043 ms | 964 ms | **249,000** | 21.0 MiB |
| JsonText: JSON stored as text (D4's fallback) | 1,128 ms | 344 ms | 340,000 | 21.0 MiB |

All three hold the same 500,000 rows; Lists and Json are equal column for column (maps, events, links). Dapper reads a
`MAP` into a `Dictionary<string, string>` property directly.

**Verdict: adjust.** Columns stay as D3/D4 say (`UUID`/`UBIGINT` ids, `BIGINT` nanoseconds, `MAP`, `LIST(STRUCT)`; but see
spike 2 on the trace id), and the writer stages maps and nested lists as JSON text that the `COPY` converts. That is faster
than building lists in .NET (the appender's list path allocates per row), keeps the file's types, and costs one
`System.Text.Json` serialization per attribute set, which the relational providers already pay. JsonText (no `MAP` in the
file) is faster again but gives up map access in SQL; not chosen.

## 2. Finding a trace by id

**Ran.** `lookup` then `lookup2`: an hour at 10,000 spans/s (36,000,000 spans, 3.6 million traces) written as 72 collector-sized
files (500,000 spans, 50 s each), compacted into one hour file in several layouts; 20 sampled traces looked up per layout (first
lookup on a new connection, then warm p50/max). A day's `trace_index` is that hour's real (trace, service) rows (21.2 million,
282 MiB) plus 23 synthetic hours of random ids, one file per hour sorted by trace id.

**First finding: `UUID` statistics do not prune.** With `trace_id UUID`, every layout cost about the same, and the 24 sorted
`trace_index` files took **2,543 ms** for one id. `parquet_metadata` shows correct, ordered min/max per row group (one candidate
row group for the probed id), yet the read cost 84 ms per file, against 6 ms for the same file with a `UBIGINT` prefix column.
DuckDB 1.5.5 writes `UUID` as `FIXED_LEN_BYTE_ARRAY(16)` and does not use those statistics for a filter. So the id was split
into `trace_hi`/`trace_lo` (`UBIGINT`, the big-endian halves) and the layouts measured again:

| Layout (one hour, 36 M spans) | File | Lookup p50 / max (ms) | First (ms) |
|---|---|---|---|
| (a) sorted `(service_name, start)`, bloom filters on the id | 2,378 MiB | 136 / 141 | 193 |
| (a) sorted `(service_name, start)`, no bloom filters | **1,534 MiB** | **159 / 168** | 201 |
| same, 122,880-row groups instead of 30,720 | 1,530 MiB | 149 (one id, `SELECT *`) | |
| (b) sorted `(service_name, trace_id)` (`UUID`; not re-run with the split id, see below) | 1,642 MiB | 188 / 195 | 254 |
| collector chunks, 72 files, before compaction (`UUID`, bloom) | 2,089 MiB | 58 / 66 | 114 |

| Day `trace_index` (24 hourly files, 21 M rows each) | Lookup p50 / max (ms) |
|---|---|
| `UUID` | 2,543 / 2,734 |
| **`trace_hi`, `trace_lo`** | **27 / 34** |

**Unhinted detail end to end** (the day's `trace_index` for the bounds, then the spans hour within them): **158 ms p50, 166 ms
max**. Hinted detail is the hour scan alone, about 150 ms. Targets were 200 ms hinted and 1 s unhinted over a day: both met.

What else was found:
- **Bloom filters are not worth it.** DuckDB writes a bloom filter only for a dictionary-encoded column, and `DICTIONARY_SIZE_LIMIT`
  applies to every column, so a limit large enough for the id dictionary-encodes the timestamps and span ids as well: the file is
  55% larger for a 15% faster lookup. A lookup is mostly a scan of the 8-byte `trace_hi` column, which is already cheap.
- **Sorting by trace id** within the service (option b) slowed the list query (88 vs 58 ms for a 2-second, one-service page) and
  is not needed once the scan is this cheap. Bounding the read per service from `trace_index` (an `OR` of `(service, start range)`
  terms) did not prune and a `UNION ALL` per service was slower (690 ms). Option (c), file locations in `trace_index`, is not needed.
- The cost scales with the spans in the trace's hours **for that tenant** (partitions are per tenant), so a smaller tenant is
  faster; a trace crossing an hour boundary reads two hours.
- **The uncompacted chunks were the fastest** (58 ms) because DuckDB reads 72 files in parallel; compaction trades a little
  lookup speed for far fewer files (spike 5).

**Compaction cost** (an input to phase 4, found here). Compacting the whole hour in one sorted `COPY` **ran out of memory** at the
7.4 GiB limit (8 GB setting), with `temp_directory` set: first with `DICTIONARY_SIZE_LIMIT 1000000` (the dictionaries of every
column, per thread, are held in memory and not spilled: one service's 4.5 M spans alone needed more than 7.4 GiB even without a
sort), then fine without it. Measured on that hour:

| Compaction | Time | Peak RSS |
|---|---|---|
| one service, copy, no sort, default dictionary limit | 3.2 s | 3.3 GB |
| one service, copy, `DICTIONARY_SIZE_LIMIT 32000`, 30,720-row groups, 8 threads | 3.8 s | 1.4 GB |
| same, 2 threads | 7.6 s | 0.47 GB |
| one service, sorted, 8 GB limit, 8 threads | 7.3 s | 9.4 GB |
| **whole hour, sorted, `memory_limit 3GB`, 4 threads** | **137 s** (263,000 spans/s) | **4.0 GB** |
| whole hour, `trace_hi`/`trace_lo` derived in the same `COPY` | 140 s | |

So an hour of 36 M spans compacts in about 2.3 minutes on 4 threads (4% of four cores over the hour), and the peak is the
memory limit plus about 1 GB. Phase 4 must set DuckDB's `memory_limit`, `threads` and `temp_directory` for the compactor
explicitly, keep the default dictionary limit, and may cut a large partition by service (the sort key's first column) into
separately sorted files to bound one `COPY`.

**Verdict: adjust.** README D3 changes: the trace id is `trace_hi UBIGINT, trace_lo UBIGINT` in every table (spans, logs,
`trace_index`, links, exemplars), converted at the edges; span ids stay `UBIGINT`. No bloom filters. Detail and logs-by-trace
read `trace_index` for the hours, then those hours' files. Phase 4 gets the memory settings above.

## 3. Manifest read order

**Ran.** `manifest <dir> <seconds> <roles>` (`ManifestSpike.cs`). One partition directory. Writers commit a file (temp + rename)
every 5-30 ms and then rewrite their own manifest (temp + rename), dropping entries the compacted manifest has folded. Compactors
(every 200-800 ms) fold each writer's files above the current `through` into a new compacted file and publish
`_compacted.<gen>.json` by **rename without overwrite** (exclusive create), so two compactors racing for a generation cannot both
win; superseded files are deleted after a 3 s grace. Readers list the writer manifests, read them, **then** list again for the
newest compacted generation, then open every file (sleeping up to 100 ms between resolving and opening, as a query plans before
it reads). Each file names the (writer, sequence) pairs it holds, so a reader checks exactly: per writer, the pairs are 1..k with no
gap and no duplicate, k never goes down between one reader's reads, and no file is missing. The negative control (`x`) reads the
compacted manifest first and waits up to 50 ms before the writer manifests.

| Run | Roles | Correct-order reads | Problems | Negative control |
|---|---|---|---|---|
| Local APFS, one process, 20 min | 2 writers (114,801 files), 2 compactors (4,624 generations, 22 races lost), 4 readers | 88,325 | **none** | 1,861 gaps |
| **Docker named volume, two containers** (one Linux kernel), 10 min | A: 2 writers, compactor, reader; B: writer, compactor, 2 readers | 33,739 | **none** | 2,824 gaps |
| macOS host + container on a **Docker Desktop bind mount** (virtiofs), 15 min | host: 2 writers, compactor, 2 readers; container: writer, compactor, 2 readers | host 33,381 / container 31,370 | host none; **container readers: 22 gaps, 6,162 regressions** | 3,529 gaps |

No run had a duplicate or a missing file, and no published generation was ever overwritten (each compactor re-reads its
generation after publishing; 0 of 10,467 publications in the three long runs were someone else's). Lost races are the exclusive create doing its job.
The longest resolve-and-read was 245 ms, well inside the grace period.

**What failed, and why.** Inside Docker Desktop's VM, a macOS bind mount is a virtiofs share with attribute and directory caching:
the container's readers saw a writer manifest older than one they had already read (regressions), and so files went missing from a
read for a moment (gaps); their last read lagged the host writers by about 170 files (3 s). The host's readers, reading files the
container wrote, stayed correct. This is not the protocol: the same roles over a volume that both containers see through one kernel
were clean. It is what to expect from any share that caches metadata (NFS with attribute caching, SMB), none of which were
available to test.

**Verdict: holds, on one machine's file system.** The read order (writer manifests, then a fresh listing for the compacted one)
is correct whenever the file system orders a rename before a later read, and the negative control shows the order matters. Plan
changes:
- **U6/layout**: the compactor's manifest is `_compacted.<gen>.json`, created by exclusive rename (`File.Move(..., overwrite: false)`,
  which was atomic on APFS and ext4), never rewritten; a reader takes the highest generation; older generations go after the
  grace period. This replaces phase 4's "generation check then discard" (a check-then-rename race) and makes the lease a
  liveness device only.
- **Supported roots (U4, U9)**: a directory on one machine's file system: a local disk, or a Docker volume / bind mount on a Linux
  host shared by containers on that host. Collectors and API hosts on **different machines** need a network share, which v1 does
  not support (unvalidated, and the virtiofs result says a caching share breaks the read order); several collectors are supported
  as several processes or containers on one machine. Docker Desktop bind mounts between the host OS and containers are not
  supported for a root shared by a process on each side (development with everything in containers, or everything on the host, is
  fine).

## 4. Concurrency, memory and cancellation in the API process

**Ran.** `concurrency <memoryLimit> 60 <workers>`: one database shared by the process (`DataSource=:memory:?cache=shared`; a
second connection sees the first one's `memory_limit`, so it is one database with one thread pool of 8), a new connection per
query, workers looping over a third each of list (one service, 2 s, newest 501), summary (raw spans over 15 minutes per service and
minute: far heavier than the rollup read it stands in for) and detail (spike 2b's lookup), against spike 2's compacted hour
(36 M spans, `trace_hi`/`trace_lo`).

| Workers | `memory_limit` | Peak working set | list p50 / p95 | summary p50 / p95 | detail p50 / p95 | Queries/s |
|---|---|---|---|---|---|---|
| 32 | 2 GB | 1,143 MiB | 279 / 350 ms | 537 / 983 ms | 734 / 1,151 ms | 24 |
| 32 | 8 GB | 1,166 MiB | 280 / 352 ms | 541 / 951 ms | 735 / 1,137 ms | 24 |
| 8 | 2 GB | 887 MiB | 232 / 275 ms | 414 / 528 ms | 567 / 676 ms | 24 |

Alone, the same list and detail take 58 and 150 ms (spike 2). Every query parallelizes across the database's 8 threads, so under
load the machine's 8 cores are the limit and latency grows with concurrency; memory never came near either limit. Throughput is
the same at 8 and 32 workers.

**Out of memory**: materializing the sorted hour with `memory_limit` 300 MB and no temp directory fails after 143 ms with
`DuckDBException` "Out of Memory Error: could not allocate block of size 256.0 KiB"; the process and the connection carry on
(`SELECT 42` answers). A query that only needs top-N (`ORDER BY ... LIMIT 10`) completed in 2.2 s within 300 MB.

**Cancellation** of a 4x regex scan of the hour, after 1 s:

| Path | Ended after | Exception | Connection reusable |
|---|---|---|---|
| `DbCommand.Cancel()` from another thread | 1,010 ms | `OperationCanceledException` | yes |
| `ExecuteScalarAsync(token)` | 1,005 ms | `OperationCanceledException` | yes |
| Dapper `CommandDefinition(cancellationToken)` | 1,007-1,017 ms | `OperationCanceledException` | (not checked) |

**Verdict: holds.** README D5/phase 3 decision 3 as planned: one shared database per API process, a connection per query.
`TimedQuery` works as it does elsewhere (the deadline cancels the token; the exception type is `OperationCanceledException`), and
the "never reuse a timed-out connection" rule costs nothing since connections are per query anyway. The defaults to set are
`threads` (the API's read budget; the compactor needs its own, see spike 2) and a `memory_limit` with a `temp_directory`; 2 GB
was ample for these reads. A dashboard of heavy raw-span reads on a large tenant's busy hour is CPU-bound at about 24 queries/s
on 8 cores, which is why the cards and charts read the rollups.

## 5. Reading many small files

**Ran.** `files`: the last 10 minutes of spike 2's hour (6 M spans) written as 1, 10, 120 and 1,000 files holding the same rows,
time slices split over two "collectors", 30,720-row groups. Explicit file lists (README D5), warm p50 of 10 (5 for summary).

| Files | Size | list (one service, 2 s) | summary (10 min, all services) | lookup by id | metadata only |
|---|---|---|---|---|---|
| 1 | 256 MiB | 11.1 ms | 48 ms | 37.6 ms | 3.8 ms |
| 10 | 279 MiB | 5.4 ms | 44 ms | 27.3 ms | 1.7 ms |
| 120 | 247 MiB | 9.4 ms | 47 ms | 28.1 ms | 3.9 ms |
| 1,000 | 241 MiB | 48.4 ms | 79 ms | 70.8 ms | 26.2 ms |

Cold (new connection) and warm were the same: DuckDB re-reads each file's footer per query (the `parquet_metadata_cache` setting
made no difference to spike 2's lookups). Writing the sets took 20-32 s.

**Verdict: adjust.** Many files are cheaper than expected: a partition of 100-odd files reads as fast as one, because the files
are read in parallel and each is pruned on its own statistics. A file count in the hundreds starts to cost (footer reads, 26 ms
at 1,000). Phase 4's `MinorFileThreshold` default becomes **100** (not about 16), and with two collectors at the default 30 s
linger (240 files per tenant-hour per table) minor compaction runs a few times an hour. U5's 30 s linger stands.

## 6. Packaging

**Ran.** `dotnet publish` of the spike (framework-dependent) per runtime identifier, and with none; the self-contained linux-arm64
publish run in a container (`postgres:18`, Debian 13, glibc), since the `mcr.microsoft.com/dotnet/sdk:10.0` pull did not complete.

| Publish | Total | DuckDB native library |
|---|---|---|
| no runtime identifier (portable) | 316 MB | **315 MB** (all five platforms) |
| linux-x64 | 68 MB | 67 MB |
| linux-arm64 | 61 MB | 60 MB |
| win-x64 | 36 MB | 35 MB |
| osx-arm64 | 113 MB | 112 MB |

The managed DuckDB.NET and Apache.Arrow assemblies add 720 KB. Licenses: DuckDB.NET MIT, DuckDB MIT. DuckDB.NET.Data.Full 1.5.5
targets net8.0 and net10.0. On linux-arm64 every spike 1 probe gave the same result as on macOS. Not checked: the
`mcr.microsoft.com/dotnet/aspnet` image itself (also glibc-based Debian, so expected to load the same way), win-x64 and linux-x64
loading (no machine), Alpine/musl (DuckDB ships no musl build in this package: Alpine images are unsupported for this provider).

**Verdict: holds** (U1 stands), with a packaging decision for phase 1: `Keryhe.Telemetry.Api.Server` and `.Collector.Server`
reference every provider, so taking `DuckDB.NET.Data.Full` into the Parquet project puts 315 MB of native libraries into every
portable publish of either host, whatever its provider. Options, in order of preference:
1. Publish the hosts with a runtime identifier (`dotnet publish -r linux-x64 ...`): the docs, `install-service.bat`'s
   instructions and any container build say so, and a publish then carries one platform (35-112 MB). A portable publish still
   carries all five.
2. The in-repo hosts set a default `RuntimeIdentifier` for the build machine when none is given, so a plain `dotnet publish`
   behaves as option 1.
3. Accept the size.
**Decided 2026-10-10 (user): option 3.** The in-repo hosts are development examples and keep every provider; a real deployment is
a consumer's own host referencing only the provider package it uses.
