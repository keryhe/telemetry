# ClickHouse write-path diagnosis: results

Run 2026-10-08. The question: is ClickHouse's low write ceiling caused by many small inserts and the materialized views
on the insert path? **Yes.** Batching alone raised the ceiling about 6×. Dropping the views raised it further and cut the
database's CPU, until the ramp ran out of steps.

## Setup

- Stress harness `ramp-write-only` profile, ClickHouse, split topology, same commit for all four runs.
- 3 tenants; base load is 1,000 spans/s, 1,000 logs/s and 1,000 data points/s; the load rises by ×1 each 60 s step, up
  to ×30.
- The database container is limited to 4 CPUs and 8 GB. The Docker VM has 8 CPUs and 8.3 GB, so the 8 GB cap is
  effectively the whole VM. The collector, API and load generator run on the same Mac.
- Plain synchronous inserts (no `async_insert`).
- New for this experiment:
  - `Telemetry:Ingestion:FlushLingerMilliseconds`: a drain loop waits, under the per-signal drain lock, until a full
    batch is queued or the time is up.
  - Harness options `--host-env KEY=VALUE` and `--db-sql <file>`.
  - Script and SQL: `stress-results/ch-diagnosis/` (gitignored), with each run's `report.html` in `a/`…`d/`.

| Run | Change |
|---|---|
| a | Today's settings (flush whatever is queued, batch cap 2,000, 4 flush loops per signal) |
| b | Linger 1,000 ms, batch caps 50,000 |
| c | b + all eight materialized views dropped (`mv_trace_index`, five `mv_metric_last_seen_*`, two rollup views) |
| d | c + `FlushConcurrency = 1` |

## Results

| Run | Sustained | Records/s at that step | Stopped by | Commit lag p95 there | DB CPU at ×4 | DB CPU at ×25 |
|---|---|---|---|---|---|---|
| a | **×4** | 12,200 | commit lag (11 s at ×5) | 2.0 s | 4.1 cores (saturated) | — |
| b | **×25** | 76,200 | gate wait + commit lag at ×26 | 1.7 s | 0.99 | 2.8 |
| c | **≥ ×30** (step limit) | 91,400 | nothing tripped | 1.6 s | 0.57 | 2.0 |
| d | **≥ ×30** (step limit) | 91,400 | nothing tripped | 2.4 s | 0.48 | 1.6 |

Average rows per insert statement (from `system.query_log`):

| Run | spans | log_records | data points (gauge/histogram) | data points (summary/exp. histogram) |
|---|---|---|---|---|
| a | 334 | 496 | 80 | 26 |
| b | 16,100 | 14,400 | 4,200 | 1,400 |
| c | 16,600 | 15,900 | 4,800 | 1,600 |
| d | 20,800 | 19,200 | 6,100 | 2,000 |

Correctness: every raw-data cell matched in all four runs. Runs c and d show 6 mismatches, all `request_rollup_minute` /
`log_rollup_minute` at 0 rows: nothing fills them once their views are dropped. That's expected.

## What it shows

1. **Small inserts were the main cause.** At the same ×4 load, the database went from 4.1 cores (its whole limit) to
   0.99 when inserts grew from hundreds of rows to thousands. The ceiling went from ×4 to ×25.
2. **The views cost about 40% of the remaining insert CPU** (0.99 → 0.57 cores at ×4, 2.8 → 2.0 at ×25) and were what
   ended run b. Without them nothing tripped by ×30.
3. **One flush loop per signal is enough** once batches are large (d matches c's ceiling with less CPU). The cost is
   about 0.8 s more commit lag at ×30.
4. **The real ceiling is above 91,000 records/s.** At ×30 the database used 1.6–2.3 of its 4 cores. A longer ramp is
   needed to find where it stops, and the load generator and collector may become the limit first on this machine.
5. **Freshness cost.** The 1 s linger puts commit lag at about 1.1–1.6 s even at low load. That's the trade for large
   inserts.
6. **For comparison:** the earlier write-only ramps (an older commit, same profile) put PostgreSQL at ×15, Timescale
   ×14, SQL Server ×10 and MySQL ×6. Those providers weren't re-run with large batches, so this isn't a like-for-like
   ranking.

## Open findings for the redesign

- **Each insert still creates about 30 parts per table** (for example, run c: 54,366 new `spans` parts from 1,798
  inserts). Tables are partitioned by day, and the harness sends backdated and late data spread over many days, so each
  batch is split across many day partitions. Real traffic is less spread out, but late data does exist. The redesign
  should decide the partition key, and what to do with data older than a day or so (separate path, clamp, or reject
  beyond retention).
- **Memory climbs to about 7 GB of the 8 GB limit at ×20 and above** in every run. It's likely mostly cache, but at
  this box size it's the next limit. Container memory doesn't separate cache from working memory, so check ClickHouse's
  own metrics before drawing a conclusion.
- **`MergeParts` shows errors in every run** (for example, 171 on `spans` in run c), possibly merges cancelled by the
  retention sweep's `DROP PARTITION`, which runs every 30 s in this profile. Not investigated.
- **The rollups, `trace_index` and `metric_last_seen` need a source** that isn't an insert-path view (from the batch in
  the collector, or a periodic job).

## Redesign targets (step 2, agreed 2026-10-08)

| Item | Target |
|---|---|
| Ingest rate | 100,000 records/s sustained (about 100 tenants × 1,000/s), with the database under about 60% CPU so bursts and merges have room |
| Freshness | Collector accept to queryable at most 5 s p95 |
| Read latency (p95, under target ingest) | Dashboard, summaries and first trace-list page under 1 s for 24 h and under 3 s for 7 d; trace detail under 300 ms; log search in the 24 h raw-search window under 3 s |
| Hardware | One node, the reference box (4 CPU / 8 GB, the harness limits). Must scale up on a bigger single node without changes; no replication or sharding |
| Retention | Global per-signal windows as today, enforced by dropping whole day partitions |
| Late data | Accept anything inside retention. The collector splits each batch by day partition: the current day flushes as one large insert, older days collect in their own buffers and flush less often. Data older than retention is dropped at the collector and counted |
| Batching | Collector-side linger (about 2–3 s), large batch caps, one flush loop per signal. No ClickHouse async inserts |
