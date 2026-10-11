# Phase 6: measurement

Part of [Parquet on object storage](README.md). Runs, not code; results in `results.md`. Emulators only (O19): every number is
labelled with its backend and injected latency, and none is presented as a cloud number.

## Runs

| # | Run | Answers |
|---|---|---|
| 1 | Local `smoke` and `ramp-write-only`, same machine as the first plan's phase 7 | Phase 1 did not regress local (within noise) |
| 2 | `smoke` on `s3` and `azure`, no injected latency, then `--storage-latency-ms 25` | Correctness ledger balances; storage requests per signal per tenant-hour (the cost model, O21) |
| 3 | `ramp-write-only` on `s3` at 0 and 25 ms | Write ceiling and `commit_lag` against local; whether O10's per-backend defaults (linger, flush concurrency, buffer size) are needed, and which |
| 4 | `ramp` (UI reading) on `s3` at 25 ms | Read latency against the O7 target: trace list and detail p95 at most local + 150 ms; 24 h dashboard summary at most 1 s p95 |
| 5 | Two collectors and one API host on `s3` at 25 ms, a 2-hour window with compaction running | Each closed hour reaches one file set; no lost or duplicated rows; manifests retired after a collector is stopped |
| 6 | Kill -9 a collector mid-flush on `s3`; restart with a new id | Lost data is the buffer only (as U2); the dead writer's manifests are retired; orphans swept after the grace |
| 7 | `--seed-days 7` on `s3` at 25 ms; trace detail with and without the hint | Day-merged reads remote; resolve time for a 7-day export window |

## The cost model

From run 2's counts: PUT, GET and LIST requests per hour as a function of tenants, signals and collectors, at the default linger and
compaction settings, with the AWS and Azure list prices of the day written next to the formula (prices change; the formula is the
deliverable). Goes into the provider README.

## Done when

`results.md` records each run, states whether O7 was met and, if not, what was changed or why it was accepted, and the README's
status is updated.
