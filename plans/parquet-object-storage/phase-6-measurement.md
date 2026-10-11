# Phase 6: measurement

Part of [Parquet on object storage](README.md). Runs, not code; results in `results.md`. Emulators only (O19): every number is
labelled with its backend, catalog provider, injected latency and cost settings (linger, inlining, collectors, affinity), and none
is presented as a cloud number. Dollar figures are request counts multiplied by the list prices of the day, written next to them.

## Runs

| # | Run | Answers |
|---|---|---|
| 1 | Local `smoke` and `ramp-write-only`, same machine as the first plan's phase 7, catalog on PostgreSQL | What the catalog on the write path costs local (O3): write ceiling, `commit_lag`, catalog statements per second and their p95, against the manifest-based numbers |
| 1b | Run 1's `ramp-write-only` with the catalog on SQL Server and on MySQL (`--catalog-provider`) | Whether any catalog provider limits the write ceiling; its README notes if so |
| 2 | `smoke` on `s3` and `azure`, no injected latency, then `--storage-latency-ms 25` | Correctness ledger balances; storage requests and catalog statements per signal per tenant-hour (the cost model, O21) |
| 3 | `ramp-write-only` on `s3` at 0 and 25 ms | Write ceiling and `commit_lag` against local at the object-store defaults (C19's linger); whether flush concurrency or buffer size also need per-backend defaults |
| 3b | `smoke` on `s3` at 25 ms with `MinorFileThreshold`/`MinorAgeSeconds` at 60/0 (the C20 default), 20/120 and 10/60 | Read p95 of the open hour against compaction requests and bytes: confirms C20 or changes the object-store defaults |
| 4 | `ramp` (UI reading) on `s3` at 25 ms, with trace detail both hinted and unhinted, read cache on and off | Read latency against the O7 target: trace list and detail p95 at most local + 150 ms; 24 h dashboard summary at most 1 s p95 (today's part from inlined rows); unhinted detail reported apart (README P3); catalog and read cache hit rates, and the GETs the read cache saves (C22) |
| 5 | Two collectors and two API hosts on `s3` at 25 ms, a 2-hour window, compaction running and the lease moving once (stop the holder) | Each closed hour reaches one file set; inlined rows move once per closed day; no lost or duplicated rows; one lease holder at a time; the new holder continues |
| 6 | Kill -9 a collector mid-flush on `s3`; restart it | Lost data is the buffer only (as U2; up to the linger, C19); its staged rows and objects are cleaned after `StagedTimeoutMinutes`; the restart registers a new writer row and ingests with no conflict |
| 6b | Toxiproxy cuts the response (not the request) of a fraction of catalog commits (a proxy in front of the catalog database) and of object PUTs, during a 30-minute `smoke` with compaction | No row lost or stored twice; no compaction deletes committed outputs; `catalog_commits{already_committed}` and `catalog_swaps{outcome=unknown_won|unknown_lost}` are non-zero (C14) |
| 6c | Stop the catalog database for **5 minutes** (longer than the whole `MaxFlushRetries` schedule) during `smoke` | Collectors hold (C6): report not ready and refuse exports with `RetryInfo`, `records_dropped` stays 0, ingestion resumes, the ledger balances; the same with MinIO stopped instead of the catalog |
| 7 | `--seed-days 7` on `s3` at 25 ms; trace detail with and without the hint | Day-merged reads remote, with and without the read cache; catalog and storage cost of a 7-day export window |
| 8 | `smoke` on `s3` with 100 tenants (low traffic each), 2 hours, compaction on | Catalog statements and storage requests per compaction pass; **no LIST outside the weekly sweep** (C15); statements per commit (two per flush: stage, commit); `parquet_files` and inlined row counts over time |
| 9 | Late data after a day merge on `s3` (the generator's backfill into a merged day) | A minor day merge adds one file and rewrites nothing (README P9), and late inlined rows become one add-on file; bytes read and uploaded per late batch |
| 10 | **The cost matrix**: run 8's 100-tenant `smoke` on `s3` at 25 ms, 1 hour each, over linger 30, 60 and 120 s x `--inline-derived` on and off, then the chosen pair with `--collectors 2` and `--tenant-affinity` on and off | Write requests per active tenant-hour (ingest and compaction) against C23's 250; catalog statements per second and inlined rows held; freshness (export to visible) p50/p95; read p95 of the open hour and the 24 h dashboard; cost per tenant-month. Confirms C19's 120 s and C18's inlining as defaults or changes them, and states the trade-off each step buys |

## The cost model

From runs 2, 8 and 10's counts: PUT, GET and LIST requests per hour, and catalog statements per second, as a function of tenants,
signals, collectors (with and without affinity), linger and inlining, split by caller (ingest, read, read cache, compaction,
clean-up, bucket sweep, retention; README P12: the arithmetic says which dominate, and that the lease and heartbeats are a few
statements a minute per process), with the AWS and Azure list prices of the day written next to the formula (prices change; the
formula is the deliverable). It says whether C23 was met at the defaults and, if not, which lever closes the gap (a longer linger
and its freshness cost, or the out-of-scope write-ahead log). Goes into the provider README's "Cost" section, with the catalog's
sizing (statements per second, rows in `parquet_files` and the inlined tables).

## Done when

`results.md` records each run, states whether O7 and C23 were met and, if not, what was changed or why it was accepted, and the
README's status is updated.
