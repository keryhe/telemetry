# Phase 0 results

Run 2026-10-08/09 on ClickHouse 25.8.33.6 (`clickhouse/clickhouse-server:25.8`) in Docker with `--cpus 4 --memory 7g`
(the Docker VM has 8 GB in total, so the harness's 8 GB limit was not available), client on the same Mac. Code and raw
outputs: `spikes/clickhouse-row-model/` (not in `Telemetry.sln`); the DDL phase 1 starts from is its `schema.sql`.

| Spike / check | Verdict | Changes |
|---|---|---|
| 1. .NET client | **Adjust** | Ids are `UUID` / `UInt64`, not `FixedString` (decision 4). `DateTime64(9)` is stored at 100 ns resolution (decision 5, confirmed 2026-10-09) |
| 2. Insert cost at the target | **Holds** | Nothing to drop; map bloom filters and promoted columns stay |
| 3. Dedup tokens across day splits | **Holds**, two rules for phase 2 | One token per INSERT statement; a re-batched set needs a new token |
| 4. Token index sizing | **Holds** | 32 KB stays; whole-word search must use `hasToken` |
| 5. Logs read in order | **Adjust** | Every logs query needs an explicit bucket predicate; lists use widening slices, not one ordered query |
| Check: trace list anchors | **Holds** | First page 62-75 ms |
| Check: metric chart read | **Holds** | Rows read within 1.05x of matching rows |
| Check: rollup column names | **Holds** | The shared rollup SQL runs unchanged |

## Spike 1: the .NET client

`ClickHouse.Client` 7.14.0 is the newest release. Through `ClickHouseBulkCopy` it wrote, and Dapper read back:
`Map(LowCardinality(String), String)` (including empty), `Nested` (events, links, exemplars, quantiles) with a `Map`
inside, `Enum8` by name, `Nullable(Float64)`, `Array(UInt64)`/`Array(Float64)`, `Bool`, `UUID` (`Guid`), `UInt64`, and a
table with `MATERIALIZED` columns it must not send (they filled from the maps, including the fallback keys). Maps read
back as `Dictionary<string,string>`.

Two things did not work:

1. **`FixedString` cannot carry raw bytes.** The driver UTF-8 encodes the value as text: `byte[]` fails (it encodes
   `"System.Byte[]"`), a Latin-1 string fails for bytes of 0x80 and above. **Change:** trace ids are `UUID` (a `Guid`
   built from the 16 id bytes), span ids and parent span ids `UInt64` (`BitConverter` over the 8 bytes). Same width,
   same bloom filters, parameters bind (`{t:UUID}`), and a round trip with the high bit set was exact. The conversion
   to and from the 32/16-character hex form lives in one place (README R8). A zero id is `Guid.Empty` / `0`. The
   `schema.sql` already uses these types. SQL-side `hex(trace_id)` no longer matches the hex id; nothing should read
   that.
2. **`DateTime64(9)` keeps only 100 ns.** The driver converts through .NET ticks: `DateTime`, `DateTimeOffset` and
   NodaTime `Instant` all lose the last two digits (`...456789` is stored as `...456700`); `long`, `decimal` and
   `string` inputs fail to serialize. There is no driver path to the full nanosecond. **Proposed:** accept it. The test
   data, the .NET SDK and the shared tests (`SeededDataBuilder.ToUnixNano` is ticks x 100) are all 100 ns-aligned;
   other SDKs lose at most 99 ns per timestamp, `duration_ns` is computed before truncation, and `trace_index` and the
   anchor query compare values truncated the same way. **Alternative if you want exact nanoseconds:** `Int64`/`UInt64`
   nanosecond columns instead of `DateTime64`, with `toStartOfFiveMinutes`/partition expressions written over
   `fromUnixTimestamp64Nano` (a larger change to the row model). **Decision (2026-10-09): accepted.** Phases 1-5 assume it.

`insert_deduplication_token` works through `ClickHouseConnection.CustomSettings` on the bulk-copy connection (see
spike 3).

## Spike 2: insert cost at the target

Load: 100,000 records/s in total (33,333/s each of spans, logs, metric points), 100 tenants x 8 services, flushed every
2.5 s per signal (about 83,000 rows per insert), plus the derived rows the design writes (`trace_index` about
16,600/s, both rollups about 1,700/s). Spans carry 8 attributes and 10 resource attributes; logs 4 attributes and 80 to
200 character bodies; points a 40/40/20 gauge/sum/histogram mix with 33,000 live series. Each run 300 s (the plan said
10 minutes; four variants at 10 minutes was 40+ minutes, and part counts and merges had levelled off by 120 s). Database
CPU is the server's own `ProfileEvent_OSCPUVirtualTimeMicroseconds` from `system.metric_log`, as a share of 4 cores.

| Variant | Raw records/s achieved | CPU avg | CPU p95 (1 s samples) | CPU p95 (10 s rolling) | Peak memory tracked | Spans on disk (10M rows) | Logs on disk (10M rows) |
|---|---|---|---|---|---|---|---|
| a. Full schema | 99,749 | 23.3% | 60.7% | 33.8% | 1.2 GB | 371.7 MB | 529.4 MB |
| b. No map bloom filters | 99,765 | 22.4% | 60.6% | 32.9% | 0.9 GB | 371.7 MB | 517.8 MB |
| c. No `MATERIALIZED` columns | 99,758 | 21.8% | 57.6% | 32.2% | 0.8 GB | 361.7 MB | 529.0 MB |
| d. Neither | 99,707 | 21.0% | 54.2% | 30.5% | 0.8 GB | 361.7 MB | 517.4 MB |

- **Verdict: holds.** The full layout needs 23% of 4 cores on average and 34% over any 10-second stretch, against the
  60% budget. Map bloom filters cost about 1 point of CPU and 2% of log size; the promoted columns about 1.5 points and
  3% of span size. Nothing needs dropping.
- Per-insert latency (client side): spans p50 847 ms / p95 1.0 s, logs 818 ms / 1.0 s, points 175-264 ms, per about
  83,000 rows. The generator itself kept up (no lag).
- Parts: at most 11 active parts in a partition, at most 3 background merge tasks; no merge backlog ("merges after
  run" 0). Rows per second per signal are well under what the 2.5 s linger would batch.
- Caveats: reads were not running; the client shared the Mac with the VM; data stayed in one or two day partitions; ids
  and values are random, so compression ratios are indicative only (about 37 bytes per span and 53 per log row, 7 times
  smaller than uncompressed). Phase 7 is the measurement of record.

## Spike 3: dedup tokens across day splits

On `gauge_points` (day partitions), with `non_replicated_deduplication_window = 1000`:

| Case | Result |
|---|---|
| One insert across two day partitions, retried with the same token | Stored once (both partitions' blocks dropped) |
| Same token, rows in a different order | Dropped |
| Same token, partly different rows | **All dropped, new rows included** (the token alone decides) |
| Retry after only the first partition's block had landed | The landed block is skipped, the missing one is stored |
| 250,000 rows in one `WriteToServerAsync`, `BatchSize` 100,000, one token | **Only 100,000 stored** (the 2nd and 3rd INSERT statements share the token and are dropped) |
| Same, `BatchSize` 300,000 | All 250,000 stored |
| Window of 1,000: a token 1,200 inserts old / the latest | Forgotten / remembered |

- **Verdict: holds.** A multi-partition insert is fully deduplicated, so a sealed batch may hold more than one day;
  splitting by day is for linger and part count, not for dedup.
- **Rules for phase 2** (now in its file): the token belongs to one INSERT statement, so `BatchSize` must be at least
  the largest flush (or the token must carry a chunk index); a retry reuses the token only for the identical batch, and
  anything re-batched gets a new token. At the target there are tens of inserts per minute per table, so a 1,000-insert
  window covers well over an hour of retries.

## Spike 4: token index sizing

4,000,000 log rows (one tenant, 24 h, bodies from five shapes: free text, access lines, JSON, short messages with
UUIDs and hashes, stack traces; a "medium" term in 1% of rows, a "rare" one in 0.05%, one unique token) into four
identical tables. 3 runs each, median; the condition cache was off (see below).

| Index | Index size | Table size | Unique token | Absent token | Common / medium / rare term |
|---|---|---|---|---|---|
| none | 0 | 180.0 MB | 245 ms, 4.0M rows | 241 ms, 4.0M rows | about 260 ms, 4.0M rows |
| 8 KB | 3.8 MB | 183.9 MB | 141 ms, 2.2M rows | 85 ms, 1.3M rows | 277-290 ms, no skip |
| **32 KB** | 15.3 MB | 195.4 MB | **38 ms, 147k rows** | **14 ms, 33k rows** | 272-280 ms, no skip |
| 64 KB | 30.1 MB | 210.1 MB | 25 ms, 33k rows | 21 ms, 16k rows | 263-289 ms, no skip |

- **Verdict: holds, keep 32 KB.** The index only helps terms that occur in few granules (an identifier, a misspelling,
  a missing word); a term in 0.05% of rows already lands in most granules, so common, medium and rare words scan the
  window whatever the size. 8 KB is too small for this data (an absent word still read 32% of the table, 1.3M rows); 64 KB
  doubles the index for a small gain. It costs 8% of the table.
- Only `hasToken(lower(body), 'term')` uses the index. A substring `LIKE '%part%'` read all 4.0M rows on every variant.
  The search compiler must emit `hasToken` for a whole-word term (phase 5).
- Scan speed: 4M rows in about 260 ms, so one tenant's 24 h at the target (about 29M logs) would take about 2 s by
  extrapolation, inside the 3 s target but not by a wide margin. Phase 7 measures it.
- **Measurement gotcha:** ClickHouse 25.x has a query condition cache (`use_query_condition_cache`, on by default) that
  makes a repeated predicate skip the granules it found empty. A first run of this spike looked excellent with no index
  at all (4 ms) because of it. Every benchmark and stress measurement of ClickHouse reads must set
  `use_query_condition_cache = 0` (phase 6 adds it to the harness; it is not a production setting).

## Spike 5: logs read in order

Same 4M rows, `logs_32k` (the row model's sort key), `LIMIT 501`, 3 runs, cache off.

**One ordered query over the 24 h window**, `ORDER BY toStartOfFiveMinutes(timestamp) DESC, timestamp DESC`: the plan
shows `ReadInOrder` with `FinishSortingTransform`, so it does read in sort order, but it does not stop early when the
filter is selective.

| Filter | Newest first | Oldest first |
|---|---|---|
| All services | 48 ms, 9.7% of table | 32 ms, 12.3% |
| One service | 9 ms, 1.7% | 7 ms, 1.4% |
| Severity >= 17 (1 in 7 rows) | 171 ms, **73%** | 159 ms, **73%** |
| Search `timeout` (common) | 220 ms, **66%** | 214 ms, **70%** |
| Search `outofmemoryerror` (rare) | 289 ms, 97% | 225 ms, 85% |
| `ORDER BY timestamp DESC` (control) | 214 ms, 100% | |

Two findings drove the adjustment:

- **A time range on `timestamp` does not prune the primary key.** The key's second part is
  `toStartOfFiveMinutes(timestamp)`; a predicate on `timestamp` alone selected 471 of 471 granules for a 5-minute window,
  and a 1-hour window read all 3.86M rows. Adding `toStartOfFiveMinutes(timestamp) >= toStartOfFiveMinutes(@start) AND
  toStartOfFiveMinutes(timestamp) <= toStartOfFiveMinutes(@end)` made it read 188k rows (7 ms vs 20 ms here; the gap
  grows with the day's volume).
- **Widening slices** (5 minutes first, x4 each time, the whole remainder once the next slice would cover half of it,
  the same rule the trace list uses; each slice is the ordered query with `LIMIT remaining` and the bucket predicate)
  stop early even when the filter is selective:

| Filter | Newest first | Oldest first |
|---|---|---|
| All services | 8 ms, 1 query, 11k rows read | 8 ms, 16k rows |
| One service | 14 ms, 14k | 7 ms, 16k |
| Severity >= 17 | 17 ms, 23k | 9 ms, 25k |
| Search `timeout` | 27 ms, 39k | 11 ms, 25k |
| Service + severity | 17 ms, 31k | 18 ms, 49k |
| Search `outofmemoryerror` (rare) | 314 ms, 3.9M (4 queries) | 295 ms, 3.5M |

A fixed one-query-per-bucket loop was worse for the rare search (865 ms, 75 queries). **Verdict: adjust.** Phase 5
reads logs with widening slices and always adds the explicit bucket predicate (it also applies to counts, facets and
exports with a time range).

## Check: trace list anchors

60,000 traces x 10 spans over 6 h (the stress harness volume), 4 services, one tenant; `trace_index` from the writer's
fold. Row model steps 1-3 (slice, `argMin` candidates, confirm against `trace_index.start_min`) plus the follow-up for
bounds, error flag and exact span count; 7 runs after warm-up.

| First page of 500 | Median | Queries | Matches whole-window `GROUP BY` |
|---|---|---|---|
| All services, newest first | 75 ms | 9 | yes |
| One service, newest first | 62 ms | 9 | yes |
| All services, oldest first | 67 ms | 9 | yes |
| One service, oldest first | 65 ms | 9 | yes |

**Holds** (under the 100 ms figure; the result set matched the brute-force definition exactly).

## Check: metric chart read

6.9M points (300 series per metric; one metric 7 days, the others 24 h; two tenants).

| Read | Matching rows | Rows read | Time |
|---|---|---|---|
| Every series of a metric, 24 h, 5 min buckets | 432,007 | 451,611 | 45 ms |
| Every series, 7 d, 30 min buckets | 3,024,000 | 3,027,229 | 71 ms |
| One series, 24 h | 1,440 | 217,423 (25 ranges, one per hour) | 8 ms |

**Holds.** The all-series read touches only the metric's rows; a single series narrows to about a granule per hour.

## Check: rollup column names

`RollupReadRepositoryBase`'s request and log SQL, run unchanged (with `intDiv`/`toInt64`) against the new tables, summed
partial rows across operations and kept severity `-1`: 17 requests, 2 errors, 17 in band 3, 7 + 2 log records. **Holds.**
Note `log_rollup_minute.severity_number` is `Int16` in the DDL (it must hold `-1`; the row model's `UInt8` could not).

## Changes applied

- Row model: decision 4 (ids), decision 5 (note on 100 ns), decision 6 (token rules), decision 10 and the logs section
  (bucket predicate, widening slices), the token-index paragraph (`hasToken`), the rollup DDL (`Int16`); the spike section
  now points here.
- Plans: README R8; phase 1 (decision 3, step 2, tests), phase 2 (decision 4, new decision on tokens and `BatchSize`),
  phase 5 (decisions 1 and 2, tests), phase 6 (harness: `use_query_condition_cache = 0`).
