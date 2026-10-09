# ClickHouse redesign: phase 7 measurements

Measured 2026-10-09 on the working tree of branch `feature/clickhouse-refactor` (uncommitted, based on `45e113b`), ClickHouse
25.8 in the harness container (4 CPU / 8 GB), split topology, 3 tenants. The Docker VM has 8 CPUs; the collector, API, load
generator and browsers share the Mac with it, so the load side competes with the hosts. No pass/fail gate; misses are listed
with what the data suggests and none was fixed in this phase.

Report folders (gitignored `stress-results/`):

| Run | Folder | What |
|---|---|---|
| A | `20261009-122742` | `ramp-write-only`, up to x55, commit-lag limit 5 s |
| B | `20261009-125336` | same, commit-lag limit relaxed to 30 s (finds the throughput ceiling) |
| C | `20261009-132443` | `ramp` (3 browsers + marker probes), freshness limit 5 s |
| D | `20261009-134231` | fixed 14,000 records/s per signal for 5 min, `--seed-days 7 --seed-spans-per-day 1000000`, 3 browsers |
| smoke | `20261009-043522` | phase 6 smoke on the finished code: ledger balanced |

Profiles: the repo's `ramp-write-only` / `ramp` with `maxSteps` 55 and `backdatedFraction` 0 (the 2% "older than retention"
records trip `maxDroppedRecords` now that the collector drops them at ingest, by design; retention is covered by smoke), `ramp`
with `maxLagSeconds` 5; D is the `fixed-x14` profile below. One load step "x1" is 3,000 records/s (1,000 spans, logs and data
points); the figures are records/s summed over the three signals. The profile files were kept in the session scratchpad;
their differences from the repo profiles are the ones just listed (D: 3 tenants, 60 s warm-up, 300 s measured, 14,000/s per
signal, 5% late arrivals, 2% redelivery, no backdating).

## Targets

| Target | Measured | Run | Met |
|---|---|---|---|
| 100,000 records/s sustained, database under about 60% CPU | **67,000** with commit lag p95 under 5 s (x22); **83,000** at the throughput ceiling (x28; x29 tripped gate wait p95 > 250 ms with no drops). Database 0.6 core (15%) at x22 and 0.75-0.85 core (20%) at x28-29 | A, B | **No** (83% of target; the database is not the limit, see below) |
| Same, with the read tour running | **42,700** sustained (x14); x15 tripped export p99 5 s, lag growth and lag over 5 s. Database CPU spikes to 1.3-3.4 cores whenever the tour reads | C | **No** |
| Freshness, accept to queryable, p95 at most 5 s | marker probes at 42,000 records/s with reads: **p50 3.0 s, p95 4.3 s, max 5.7 s** (log/trace, 83 probes). Commit lag p95 about 3.4 s up to 43,000 records/s write-only, 5.1 s at 70,000 | D, A | **Yes** at 42,000 records/s; at higher rates the 2.5 s linger plus flush time passes 5 s above about 67,000 |
| Dashboard, summaries, first trace-list page: under 1 s (24 h), under 3 s (7 d) | Browser page-ready, worst of 3 samples: dashboard 24 h 186 ms, 7 d 379 ms; `traces` 24 h 301 ms, `traces:list` 24 h 875 ms, 7 d 456 ms; summaries (`traces/summary`, `logs/summary` routes, ramp tour up to x14) p95 205 / 145 ms | D, C | **Yes**, but see the history caveat |
| Trace detail under 300 ms | p95 of 9-span history trace 37 ms; 1,000 spans 53 ms; 5,000 spans 108 ms; **20,000 spans 341 ms** (hinted 347 ms). Detail pages in the tour 106 ms max | D | **Yes** up to 5,000 spans; **No** for the 20,000-span trace (the response is 12.7 MB) |
| Log search in the 24 h raw-search window under 3 s | `logs:search` 24 h: p50 929 ms, max 1,452 ms; `logs:attribute-search` 929 ms; 7 d `logs:search` 193 ms (outside the window the API answers 400 by design) | D | **Yes**, on the data volume below |
| One node, scales up unchanged | one container, no replication or sharding | all | Yes |
| Retention: day-partition drops | sweeps ran 54 times in D with no errors; `RetentionTestsBase` passes (phase 6) | D | Yes |
| Late data accepted, current day one large insert, older days their own | 5% late arrivals offered in every run; `late_buffer_records` peaked at 199,995 and drained; no records dropped and every ledger cell matched (27 of 27). About 115 `NewPart`s per points table and 118 for `log_records` over the run, 43,000 rows per part | D | Yes (the generator's 24 h backfill was not run, see below) |
| Collector-side batching, no async inserts | 1 flush loop per signal, 2.5 s linger, up to 144,000 spans per insert at x28 | B | Yes |

## What the ramps show

- **The ceiling is in the collector, not the database.** In run B the database used 0.5-0.9 of its 4 cores up to the ceiling
  while the collector rose from 0.8 to 1.5 cores and its trace flush p95 grew from 0.4 s (x10, 30,000 records/s) to 1.4 s (x23)
  to 3.5 s (x29) as batches grew from 30,000 to 148,000 spans. One flush loop per signal builds the rows and sends a batch;
  once a flush takes longer than the 2.5 s linger, the queue grows (gate wait, then export latency). Commit lag is linger plus
  flush time, so the 5 s lag limit trips first, at about 67,000 records/s. A second flush loop per signal or splitting a large
  batch into concurrent inserts would be the first thing to try; neither was tried here. The diagnosis (run d) reached x30 with
  the same single loop and about 91,000 records/s, but with 1 s linger and no rollup, `trace_index` or catalog writes at all.
- **Reads cost the write path about half its ceiling** (83,000 without reads against 42,700 with the tour). The tour's reads
  (three users walking every page and window) push the database to 1.3-3.4 cores in the affected steps, and the write
  path's commit lag follows. In run D at 42,000 records/s the database averaged 1.25 cores (31%), p95 3.2, and touched 4.2.
- **Memory** peaked at 3.1 GB of 8 GB in D and 2.8 GB in B (the diagnosis runs reached about 7 GB).
- Correctness: every run balanced (27 of 27 cells in A, B and D) with no mismatches.

## Misses and what the data suggests

1. **100,000 records/s.** The ceiling is the collector's flush path (above). Try concurrent flushes or insert pieces first;
   the database has room (about 80% of its CPU unused at 83,000).
2. **Reads under load.** The costly reads in D's `system.query_log` are the log list (318,000 rows read per call, 1,117
   calls, 35 ms mean), the log attribute-facet query (18 calls, 198 ms mean) and trace-list anchor queries. The request rollup
   query reads about 90,000 rows per call. Which queries cause the CPU spikes was not isolated.
3. **Log facets time out at 6 h and 7 d** in D: 6 of 330 tour steps timed out and 3 errored, all `logs:facets-open` /
   `logs:facet-values` at 6 h or 7 d (the facets query is bounded by the 5 s summary budget and reports `timedOut`). The facet
   values step then waits 60 s for an element that is never drawn. Not a redesign target. I did not run the same tour on the old layout, so
   whether this is a regression is unknown; it is worth a look before merging.
4. **20,000-span trace detail** is 341 ms, because most of it is serialising 12.7 MB; the reads themselves are the 5,000-span
   figure scaled.

## Caveats (what this does not prove)

- **History is thin.** Run D seeded 7 days of **spans only** (7 million, 1 million per day, held in memory by the seeder).
  Logs and metrics in every run cover the minutes of the run, so the 24 h / 7 d log and metric reads here read small tables.
  At 100,000 records/s a day is 8.6 billion rows per signal; nothing here measures reads over that. Only the span, trace-index
  and request-rollup 7-day reads (dashboard, trace list, summaries) are over seeded history. The hour rollup tier trigger (7-day
  reads over 3 s) did not fire on this data; it may on realistic volume.
- **The plan's late-data step was not run as written.** Step 4 asks for the generator's 24 h backfill beside live load. It
  needs tenants and keys in a dev database and was not set up; the harness's 5% late arrivals stand in, and show late buffers
  filling and draining without drops, but they are small (the buffer peaked at about 200,000 records).
- Steps 2-3's read latencies are three samples per page and window from the browser tour, not a read-only latency test.
- The load generator, hosts and Docker VM share one Mac; a dedicated box would raise the collector's ceiling.

## Comparison with the diagnosis runs (`plans/clickhouse-diagnosis-results.md`)

| | Diagnosis a (old layout, defaults) | b (linger, large batches) | c/d (also no views) | This build, write-only |
|---|---|---|---|---|
| Sustained | x4 (12,200/s) | x25 (76,200/s) | at least x30 (91,400/s, step limit) | x22 (67,000/s) under a 5 s lag limit; x28 (83,000/s) with it relaxed |
| Database CPU at x25 | saturated at x4 | 2.8 cores | 2.0 / 1.6 cores | about 0.7 core |
| Rows per insert (spans) | 334 | 16,100 | 16,600 / 20,800 | 30,000-148,000 |

The database now uses about a third of the CPU it did in c at x25 (0.7 core against 2.0), with the rollups and catalog written by the
collector instead of materialized views; the work moved to the collector.

## Parallel flush (added after phase 7)

Phase 7's profile of one ~73,000-span flush (60,000 records/s write-only): row building ~0.3 s, the raw insert ~0.55 s (ClickHouse
itself ~90 ms per part; the rest is the driver serializing and sending rows on one thread), derived inserts ~0.1 s, all on one
thread. A temporary compression-off test saved only 5-10%. So, **for large batches only**, the writer now builds spans and logs on
several threads and splits each raw-table insert of at least `ParallelFlushMinRows` (50,000) rows into up to `MaxParallelInserts`
(4) concurrent inserts of at least `InsertPieceRows` (25,000) rows, each its own connection and dedup token
(`{token}:{table}:{i}`). Smaller batches are unchanged: one insert, same token, same parts. Metrics are split at insert time but
still built on one thread. Not committed; code in `ClickHouseBulkWriter.cs`, options in `ClickHouseIngestionOptions.cs`, tests in
`ClickHouseParallelFlushTests`.

| Write-only ramp, commit-lag limit 30 s (run B before: `20261009-125336`, after: `20261009-153821`) | Before | After |
|---|---|---|
| Ceiling (last step before the gate-wait trip) | x28, 83,300 records/s | **x31, 94,100 records/s** (x32 offered 102,700, acked 92,200, gate wait p95 424 ms) |
| Trace flush p95 at x23 / x28 | 1.44 s / 2.57 s | **0.66 s / 1.19 s** |
| Commit lag p95 at x23 (traces) | 5.1 s | 3.7 s |
| Rate where commit lag p95 first exceeds 5 s (any signal) | x23 (70,000/s) | x27 (about 82,000/s: metrics 5.2 s, traces 4.3 s) |
| Database CPU at x28 | 0.73 core | 1.0 core |
| `NewPart` count over the whole run: spans / logs / gauge | 543 / 585 / 569 | 1,175 / 1,147 / 629 |
| Ledger | 27 of 27 | 27 of 27 |

- The rate at which freshness stays under 5 s moved from about 67,000 to about 80,000 records/s; the 100,000 target is still
  not met.
- **Metrics are now the slowest signal**: their flush p95 is 1.9 s at x28 and 5.4 s at x32 (traces 1.2 s and 2.7 s), and they
  set the commit lag from x27. Their rows are still built on one thread, and the build also updates the shared catalog
  tracker. Making that build parallel is the next candidate.
- Splitting doubled the parts written for spans and logs (about 1,150 against 550 over 35 minutes), and total merge time roughly doubled
  (spans 90 s to 173 s), with no errors and no TOO_MANY_PARTS.
- **Reads, fixed 14,000 records/s per signal with 7 days of seeded spans (before `20261009-134231`, after `20261009-161300`):**
  unchanged within noise. Freshness p50 3.0 s / p95 4.1 s (before 4.3 s), database CPU average 1.28 cores (1.25), p95 3.26 (3.22),
  dashboard 24 h 178 ms (186), 7 d 271 ms (379), first trace-list page 24 h 966 ms (875), 7 d 409 ms (456), `logs:search` 24 h
  1.0 s (1.45 s), 20,000-span trace detail p95 338 ms (341). `NewPart`s were 133 / 129 for spans / logs (108 / 118): at this
  rate flushes are mostly under the split threshold, so this run mostly shows the normal path is unchanged.
- **Not measured:** reads while a high rate (above about 60,000 records/s) is splitting batches into extra parts; the ramp with
  browsers was not rerun, because it stopped at x14 where splitting rarely applies.

### Metrics build and points tables (second round)

Two further changes for large batches only (still the same `ParallelFlushMinRows` threshold, by data points for metrics): a metrics
batch is built on several threads, each chunk with its own row lists and catalog/series notes merged in order
(`MetricRows`, `MetricDerived.Merge`; test checks `metric_catalog`/`metric_series` equal the single-pass result), and the five
points-table inserts of one batch run together instead of one after another. Same write-only profile, commit-lag limit 30 s.

| Run | Folder | Ceiling (last sustained step) | Tripped by | Metrics flush p95 at x24 / x27 |
|---|---|---|---|---|
| Parallel flush, spans and logs only | `20261009-153821` | x31, 94,100/s | gate wait at x32 | 1.06 s / 1.50 s |
| + parallel metrics build | `20261009-170124` | x30, 92,200/s | gate wait and export p99 at x31 | 0.87 s / 1.42 s |
| + concurrent points tables | `20261009-173627` | x27, 82,300/s | gate wait at x28 | 0.73 s / 1.22 s |

- Metrics flush p95 fell by about 20-30% at moderate rates, as hoped. The ceiling did **not** rise: the three runs stopped at x27 to
  x31 (82,000-94,000 records/s), which I read as run-to-run variation, not an effect of the changes. Near the ceiling the collector
  (about 1.5 cores), the load generator, the API host and the Docker VM share the Mac's 8 CPUs, and the database is at 0.8-1.7
  of its 4 cores, so the next limit on this machine is probably the machine itself. Separating the load generator from the
  collector and database is the way to tell; I did not do that.
- Ledger: 27 of 27 cells in every run. Tests: 127 ClickHouse tests pass.
- Net effect of all parallel-flush work on the write-only ceiling: x28 (83,000/s) to somewhere in x27-x31 (82,000-94,000/s); on
  commit lag at moderate rates (60,000-80,000/s) a clear improvement (traces p95 at x23: 5.1 s to 3.7 s). The 100,000 target
  is not met on this machine.
