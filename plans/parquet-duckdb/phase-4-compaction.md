# Phase 4: compaction

Part of the [Parquet + DuckDB provider](README.md). Keeps the file count, and so read cost, bounded: every linger of
every collector adds a file per tenant-hour per table. Runs on the API host (U3), generic over the table definitions
(README D7), so phase 5's tables are compacted with no extra code.

## Decisions

| # | Decision |
|---|---|
| 1 | **`ParquetCompactionWorker`** (a `BackgroundService` registered by `AddParquetApiServices`, gated by `Telemetry:Parquet:Compaction:Enabled`, default true) wakes every `IntervalSeconds` (60) and works only while it holds the lease |
| 2 | **Lease**: `_leases/compaction.lease` holds the owner id and an expiry, written by temp + rename; an owner renews every interval, another instance takes it over once it has been expired for `LeaseSeconds` (300). The lease is a liveness device, not a lock: two compactors for a short overlap are made harmless by decision 6 (phase 0 spike 3 ran two compactors racing continuously: 48 lost races over three runs, no damage) |
| 3 | **Minor compaction** (any partition, including the open hour): when a partition's uncompacted writer files exceed `MinorFileThreshold` (100; phase 0 spike 5: 120 files read as fast as one, 1,000 about 5x slower) or are older than `MinorAgeSeconds` (600), fold them into one new compacted file, sorted by the table's sort key, and advance each writer's `through` to the highest sequence folded |
| 4 | **Major compaction**: an hour closed for `CloseHourGraceMinutes` (15) is rewritten into one file set (split at `MaxFileRows`, 10 million); a day closed for `CloseDayGraceHours` (6) is merged into `_day/` and its hour manifests point there. Late files arriving after a major compaction are writer files with a higher sequence, picked up by the next minor or major pass |
| 5 | **Rollup tables are pre-summed** on compaction (`GROUP BY` the key columns, `SUM` the counts, `MAX` the max), so a compacted rollup has one row per key; reads still `SUM` (README D8). `trace_index` is merged by (tenant, trace, service) with min/max/or/sum; `metric_catalog` and `metric_series` by their keys with min first-seen and max last-seen |
| 6 | **Publish order**: write the compacted file (temp + rename, a name unique to the compactor), then publish `_compacted.<gen+1>.json` with the new file list and `through` values by **rename without overwrite** (`File.Move(tmp, final, overwrite: false)`). If that generation already exists another compactor won: delete the new file and start over from the newer generation. A generation file is never rewritten; superseded generations are deleted after the grace period (phase 0 spike 3) |
| 7 | **Deleting**: a superseded file (a writer file at or below `through`, or a replaced compacted file) is deleted once it has been superseded for `GraceSeconds` (600, above the longest query: the export timeout is unbounded, so an export holds a read of the file list it resolved, and a file deleted under it ends the export with an error the client sees; the README says so). Writers drop their own manifest entries at or below `through` on their next commit to that partition. Orphans (files in no manifest, older than the grace period, `.tmp` files of dead writers) are deleted too |
| 8 | **Bounded memory (phase 0 spike 2)**: the compactor uses its own DuckDB database with `Compaction:DuckDb:MemoryLimit` (3 GB), `Threads` (4) and a `temp_directory`, and never raises `DICTIONARY_SIZE_LIMIT` (dictionaries are not spilled: a raised limit ran out of memory on 4.5 M spans). A partition above `MaxSortRows` (20 M) is compacted per `service_name` (the sort key's first column) into separately sorted files, so one `COPY` stays bounded; an hour of 36 M spans in one sorted `COPY` took 140 s at a 4 GB peak |
| 9 | **Bounded work**: at most `MaxConcurrentCompactions` (2) partitions at a time, through the engine's memory limit; a partition that fails is logged, counted (`compaction_failures`) and retried next interval with back-off; it never blocks other partitions |
| 10 | Instruments on a `Keryhe.Telemetry.Parquet` meter: `compaction_duration` (by kind), `files_compacted`, `files_deleted`, `uncompacted_files` (observable gauge, the largest partition's count), `compaction_failures` |

## Steps

1. Lease and generation handling.
2. Partition scanner: candidates for minor and major passes from the manifests' counts and ages.
3. Compactor: one DuckDB `COPY (SELECT ... FROM read_parquet([...]) ORDER BY ...) TO ...` per output file, the
   pre-sum queries for the derived tables from their definitions.
4. Grace deletes and orphan sweep; writer-side manifest pruning in `ManifestWriter`.

## Tests

- Row counts and content are identical before and after minor, major and day compaction, for spans, `trace_index` and
  the request rollup (rollup sums unchanged, row count reduced).
- Concurrency (the phase 0 spike 3 harness, now against production code): writers committing, two compactors with
  overlapping leases, readers counting; no lost or double-counted rows, no missing file inside the grace period.
- Lease takeover after expiry; a stale-generation compactor discards its work.
- The phase 3 shared trace suites pass after a forced compaction of everything they seeded.

## Done when

A day of test data generator traffic from two collectors leaves each closed day at one file set per table and tenant,
and the trace suites pass over compacted data.

## Implementation note (2026-10-10, uncommitted)

Done: `ParquetCompactor` (scan, minor/major/day compaction, grace deletes, orphan and temp sweep), `ParquetCompactionLease`,
`ParquetCompactionWorker` (renews the lease during a pass; losing it cancels the pass), `ParquetMetrics`, `CompactionOptions`,
`ParquetManifests` (the reader's resolution, shared by the file resolver, the retention count and the compactor),
`ManifestWriter` pruning, store operations for listing files, temp files and the lease text, merge descriptors on `ParquetTable`.

Tests: `tests/Keryhe.Telemetry.Parquet.Tests/CompactionTests` (14; the concurrency one, two writers, two compactors and two readers
for 8 s, was negative-controlled by reading the generation before the writers' manifests: it then reports lost rows) and the
`ParquetCompacted*` classes of the integration suite (`ParquetCompactedFixture`: every span flush is followed by a forced compaction
with a clock two days ahead and all thresholds zero, so the trace, export, search and rollup suites read day-merged files).

Differences and findings:
- **The day generation is the switch.** Decision 4 left "its hour manifests point there" open. A day generation (`_day/_compacted.<gen>.json`)
  carries `hourThrough[hour][writer]`; an hour listed there is read from the day's files plus its writer files above those values and
  its own generation is ignored, so publishing one file moves 24 hours atomically, and the same read order (writers, then generations)
  keeps it correct. A late writer file is not folded by an hour minor compaction (the hour is covered): the next day pass takes it,
  once it is older than `MinorAgeSeconds`.
- **Grace is counted from the superseding generation's `createdAtUnixMs`**, not file times, and a file is deleted when the first
  retained generation that folds it is past the grace (so a writer that went idle, which never prunes its manifest, does not keep
  folded files alive). Orphans and temp files use at least an hour (a `COPY` writes minutes before its generation is published).
- **Day files are pruned by the manifest's min/max of the partition time** against the hours in scope, so a one-hour query does not
  open a day's every file.
- **Large compactions** write each range to a directory with `ROW_GROUPS_PER_FILE` (DuckDB writes row groups of at least 2,048 rows
  and starts a file after that many). `trace_index` merges keep the hour of `start_min` in the key, so an hour's rows mean the same
  after compaction.
- **A pass is bounded:** the last `ScanDays` days each interval, every partition once per `FullScanMinutes`.
- **Not done / for later:** the Parquet stress harness (phase 6) is where "a day of generator traffic from two collectors leaves each
  closed day at one file set" is checked; a day with late files is rewritten whole (cost follows the day, not the late file);
  `ParquetCompactor` creates the DuckDB database lazily and holds it for the process life.
