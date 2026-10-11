# Phase 7: measurement

Part of the [Parquet + DuckDB provider](README.md). Runs, not code. Results go in `plans/parquet-duckdb/results.md`
for the user to decide on; nothing is merged automatically.

## Runs

| Run | What it shows |
|---|---|
| `--provider Parquet --profile ramp-write-only` | The write ceiling with nothing reading: records/s at which `commit_lag` or gate wait trips |
| `--provider Parquet --scenario ramp` | The ceiling with the UI reading; the difference from the write-only ramp is what reads cost writes (both processes share the machine's CPU here, unlike a database container) |
| `--seed-days 14` plus the three large traces | Reads over history: list, summary, detail with and without the hint, export, against compacted days |
| Two collectors on one root | Per-writer manifests under load; correctness ledger balances |
| Compaction off, then on | Read latency growth with file count, and compaction's CPU cost |
| Kill -9 the collector mid-ramp | What a crash loses (expected: at most one linger of buffered records) and that the restart is clean |

Same machine and comparable profiles as the PostgreSQL and ClickHouse results, so the comparison page reads across.

## Targets (proposed; the user sets the final ones)

- Write ceiling at least PostgreSQL's on the same machine.
- Freshness (enqueue to readable) p95 under 45 s at the default linger.
- Read p95 within the shared budgets (summary 1 s over 7 days; a list first page 500 ms; hinted detail 200 ms).
- API host memory within `DuckDb:MemoryLimit` plus the host's own working set; no out-of-memory process kill.

## Done when

`results.md` records every run against the targets, with what failed and the options for each failure.
