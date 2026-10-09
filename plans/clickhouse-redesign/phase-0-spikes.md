# Phase 0: spikes

**Status: done 2026-10-09; results in [phase-0-results.md](phase-0-results.md), verdicts applied to the row model and to the phase files below.** Timestamps at 100 ns (README R9) were confirmed.

Part of the [ClickHouse redesign](README.md). Runs the five spikes listed in the row model ("Spike before the plan")
on ClickHouse 25.8 LTS before any production code changes (P5). Each spike names the decisions it can overturn and
what a failure triggers. Phases 1 to 7 do not start until this phase's results are recorded.

## Decisions

| # | Decision |
|---|---|
| 1 | Spikes are throwaway: a console project under `spikes/clickhouse-row-model/` (not in `Telemetry.sln`) or ad hoc SQL scripts beside it. Nothing from them is merged into `src/` as-is |
| 2 | They run in Docker on `clickhouse/clickhouse-server:25.8` with the stress harness's limits (4 CPU, 8 GB), so the numbers compare with the diagnosis runs |
| 3 | The DDL used is the row model's, written out in full (all promoted columns, all indexes) as `spikes/clickhouse-row-model/schema.sql`; phase 1 starts from it |
| 4 | Results go in `plans/clickhouse-redesign/phase-0-results.md`: one section per spike with what was run, the numbers and a verdict (holds, adjust, fails) |
| 5 | A spike that fails stops the plan: the row model is amended first (the affected decision rewritten, with the evidence), then the phases that depend on it are rewritten before they start |

## Spikes

### 1. The .NET client

Confirm that `ClickHouse.Client` (7.14.0, the version pinned today; check whether a newer release or its successor
package is needed for 25.8) bulk-writes, through `ClickHouseBulkCopy`:
- `Map(LowCardinality(String), String)`, including an empty map;
- `FixedString(16)` and `FixedString(8)` from `byte[]`;
- `Nested` columns (events, links, exemplars, quantiles), including a `Map` inside `Nested`;
- `DateTime64(9, 'UTC')` with full nanosecond precision (write a value with a non-zero last digit and read it back);
- `Enum8` from its name or number; `Nullable(Float64)`;
- a `MATERIALIZED` column it must not send;
- an `insert_deduplication_token` per insert (query setting on the bulk copy's connection or command).

And that Dapper reads back `Map` as a dictionary and `FixedString` as `byte[]`.

**Can overturn:** row model decisions 2, 3, 4, 5, 8; README R2. **If it fails:** first look for a workaround in the
driver (`RowBinary` written by hand, `JSONEachRow` over HTTP for one type); if none, change the column type (for
example `Array(Tuple(...))` instead of `Nested`, `String` hex ids) and amend the row model.

### 2. Insert cost at the target

Generate rows shaped like the test data generator's (attribute counts and sizes from a sample of today's spans, logs
and points) and insert at 100,000 records/s for 10 minutes in batches of the size phase 2 will send (2 to 3 s of
linger: 200,000 to 300,000 rows per insert, split by signal). Record database CPU, memory, part count and merge
backlog over time. Repeat without the map bloom filters, and without the `MATERIALIZED` columns, to see what each
costs.

**Can overturn:** row model decision 3 (promoted columns), the attribute bloom filters, codecs. **If over 60% CPU:**
drop the map bloom filters first (the row model says so), then trim the promoted list; record which.

### 3. Dedup tokens across day splits

Insert a batch whose rows span two days (two partitions) with a token, then re-send the identical batch with the same
token; confirm no rows are added in either partition. Then send the same rows with the same token in a different
order, and a batch that partly overlaps. Confirm `non_replicated_deduplication_window = 1000` is enough for the
expected inserts per window (count inserts per second at the target from spike 2 and work out how far back a retry
can be).

**Can overturn:** row model decision 6; phase 2's day split (one insert per day versus one per batch). **If a
multi-partition insert is not fully deduplicated:** phase 2 inserts one day per insert (it splits by day anyway), with
a per-day token.

### 4. Token index sizing

Load a day of realistic log bodies (from the test data generator, plus a sample of free text: stack traces, JSON
bodies) and measure, for `tokenbf_v1` sizes 8 KB, 32 KB and 64 KB: index size on disk, granules skipped for a
whole-word search of common and rare terms, and query time in the 24 h window.

**Can overturn:** row model decision 11 (index parameters only). **If no size is worth it:** keep the index at the
cheapest useful size, or drop it and rely on the 24 h search window as today.

### 5. Logs read in order

With a day of logs in the row model's layout, run the newest-first and oldest-first list queries (with and without a
service filter, with a minimum-severity filter, with a search term) and check with `EXPLAIN PIPELINE` and
`system.query_log` (`read_rows`) that the read stops after `limit + 1` rows rather than reading the window. Find the
exact query shape that does (`ORDER BY toStartOfFiveMinutes(timestamp) DESC, timestamp DESC`, `optimize_read_in_order`,
any setting it needs).

**Can overturn:** row model decision 10 (log sort key). **If it reads the whole window:** read bucket by bucket from
C# (a five-minute bucket per query, newest first, until `limit + 1` rows), and record that shape for phase 5.

## Also checked here

These are cheap and settle open points before phase 1:
- **The trace list anchor query** (row model "Trace list anchors"): run steps 1 to 3 against a day of spans and a
  `trace_index` built from them; confirm a first page reads one slice plus point lookups, in under 100 ms at the
  stress harness's seeded volume.
- **The metric chart read** (row model "Metric data points"): every series of one metric over 24 h and 7 d reads one
  range per hour; record rows read.
- **Rollup column names** (README R5): confirm `RollupReadRepositoryBase`'s SQL runs unchanged on the new rollup
  tables.

## Done when

- `phase-0-results.md` has a verdict for each of the five spikes and the three checks.
- Every "adjust" or "fails" verdict has been applied to the row model, and the phase files below that depend on it
  have been updated.
- `spikes/clickhouse-row-model/schema.sql` is the DDL phase 1 starts from.
