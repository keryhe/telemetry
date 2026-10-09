# Phase 7: measurement

Part of the [ClickHouse redesign](README.md). Measures the finished provider against every redesign target and records
the result. There is no pass/fail gate and nothing is committed or merged by this phase (P8): the user reads the
results and decides.

## Decisions

| # | Decision |
|---|---|
| 1 | Runs use the reference box and the harness's limits (4 CPU, 8 GB for the database), so they compare with `plans/clickhouse-diagnosis-results.md` |
| 2 | The ramp's step limit is raised past ×30 (the diagnosis runs hit the limit, not a ceiling) until a stop criterion trips or 150,000 records/s is reached |
| 3 | Freshness (accept to queryable) is measured, not inferred: the harness's marker probes record the time from export accepted to the record being visible through the API, p95 per step |
| 4 | Results go in `plans/clickhouse-redesign/results.md`, one row per target with the measured value, the run it came from and whether it was met; misses are listed with what the data suggests, not fixed in this phase |

## Steps

1. `ramp-write-only` on ClickHouse: write ceiling, database CPU at 100,000 records/s, commit lag, late-buffer depth.
2. `ramp` on ClickHouse on the same commit: the cost of reads to writes; read p95s per route at the target rate.
3. `--seed-days 7` run for 7-day reads (dashboard, summaries, first trace-list page) and the large-trace detail probes.
4. Late-data check: the generator's backfill (`Generator:Backfill:Enabled=true`, 24 h window) while live load runs at the
   target; record that late days flush in their own inserts and that the live freshness holds.
5. Log search in the 24 h raw-search window under target ingest.
6. Write `results.md`; compare with the diagnosis runs a to d.

## Targets recorded against

From `plans/clickhouse-diagnosis-results.md` ("Redesign targets"): 100,000 records/s sustained with the database under
about 60% CPU; freshness at most 5 s p95; dashboard, summaries and first trace-list page under 1 s (24 h) and 3 s
(7 d); trace detail under 300 ms; log search in the 24 h window under 3 s; one node; day-partition retention; late data
accepted; collector-side batching. If the 7-day reads miss 3 s, say so: that is the trigger for the hour rollup tier
(row model decision 15), which is a separate plan.

## Done when

- `results.md` has a measured value for every target, and the report folders are listed in it.
- The user has the results to decide on merging.
- Phase 7 measurements: see [results.md](results.md).

Follow-up: the write ceiling was the collector's single-threaded flush, so a parallel flush for large batches was added
(see "Parallel flush" in [results.md](results.md)): write-only ceiling x28 to x31 (83,000 to 94,100 records/s).
