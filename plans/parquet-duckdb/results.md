# Parquet + DuckDB: phase 7 measurements

Measured 2026-10-10 on the uncommitted working tree (based on `8dfbb36`), split topology, 3 tenants, one Mac (8 CPUs, 16 GB) shared by
both hosts, the load generator, the browsers and Docker (a PostgreSQL control-plane container only; Parquet has no database
container, so there are no database CPU/memory figures). Defaults throughout (30 s linger, compaction on, `DuckDb:MemoryLimit` 2 GB on
the API) unless stated. One load step "x1" is 3,000 records/s (1,000 spans, logs and data points); figures are records/s summed over
the three signals. Folders are in the gitignored `stress-results/`.

Profiles are the repo's `ramp` / `ramp-write-only` with three changes (copies were kept in the session scratchpad): `backdatedFraction` 0
(records older than retention are dropped at ingest by design), `maxCommitLagP95Ms` 90,000 (the repo's 5 s is below Parquet's 30 s linger,
so the repo profile trips at step 0: folder `p7-A-write-only`, a profile mismatch and not a result), and the read ramp's `maxLagSeconds`
45 (the plan's freshness target).

| Run | Folder | What |
|---|---|---|
| A2 | `p7-A2-write-only` | write-only ramp, default gates |
| B | `p7-B-ramp` | read ramp (3 browsers + marker probes), default gates |
| C | `p7-C-seed14` | fixed 9,000/s, 14 days of seeded spans + 3 large traces, 3 browsers |
| D | `p7-D-write-only-bigq` | write-only ramp, ingestion gates raised to 1,000,000 records / 2 GB per signal |
| E | `p7-E-ramp-bigq` | read ramp, same raised gates |

## Targets

| Target | Measured | Met |
|---|---|---|
| Write ceiling at least PostgreSQL's on the same machine | **64,000 records/s** write-only (x21, commit lag p95 38 s, gate wait 0); x22 tripped gate wait and export p99 (run D). With default gates only **18,000** (x6, A2/B). **No PostgreSQL run was made in this phase**, so the comparison is not done | Unknown; see the gate finding |
| Same with the UI reading | **34,000-36,000** (x11-x12; x13 tripped, run E): reads cost the write path about 44% of its ceiling (64,000 -> 36,000). With default gates the ceiling was x6 both with and without reads, so there the gate hid the read cost | Informational |
| Freshness (enqueue to readable) p95 under 45 s | Marker probes (p50 only extracted): **log 15-18 s, trace 17-18 s** at 9,000/s fixed (C) and in the ramps. Commit lag p95 is 29-39 s up to 60,000/s write-only, **49 s** in run E and 64-124 s past its ceiling. **18 marker probes timed out in E** (above the ceiling). A p95 of the probes was not extracted | Commit lag **yes** to about x20 write-only and x10 with reads; **no** beyond |
| Summary under 1 s over 7 days | `traces/summary` p95 83 ms, `logs/summary` p95 41 ms (C, tour windows up to 7 d over 14 days of spans); 54 ms in E at ramp load | Yes |
| First list page under 500 ms | `traces/list` p50 282 ms, p95 318 ms in C (steady 9,000/s); **p50 1,494 ms, p95 1,547 ms in E** (ramp load); `logs/list` p95 67 ms (C), 142 ms (E) | Yes at steady load; **No** under ramp load |
| Hinted detail under 200 ms | 7 spans 5 ms, 1,000 spans 17 ms, 5,000 spans 53 ms (p95), **20,000 spans 206 ms** (12.7 MB). Unhinted: 58 / 63 / 102 / 242 ms | Yes except 20,000 spans (206 ms, borderline) |
| API memory within `DuckDb:MemoryLimit` + working set; no OOM kill | No OOM kill or host crash in any run. Per-process memory was **not recorded** (the harness has no container stats for Parquet, and I did not read the host metric files) | Not shown |

## Findings

1. **The default gates cap Parquet at about 18,000 records/s.** `Telemetry:Ingestion:MaxQueued*` defaults to 200,000 records per signal;
   the 30 s linger holds each signal's records resident, so the gate fills at about 200,000 / 30 s = 6,700 records/s per signal. The
   collector warns about this at startup (`MaxQueued* is below Telemetry:Parquet:MaxBufferRecords`), readiness went `Unhealthy`
   ("an ingestion queue has been full") 288 times in run C, and the ceiling was x6 with and without reads. Raising the gates to 1,000,000
   records / 2 GB moved the write ceiling from 18,000 to 64,000. **Option:** make the Parquet registration default the gates to at least
   `MaxBufferRecords` x 2 (and the byte gate to match) instead of only warning; or ship the raised values in the Collector README.
2. **Reads cost the write path** (64,000 -> 36,000) and the first trace-list page grows to about 1.5 s under that load. Both processes share
   the machine's CPU, so some of this is the harness; it was not isolated further (no profiling of which DuckDB statements take the CPU:
   `parquet-sql` Debug lines are in each folder's `host-api.log`).
3. **Seeding is slow:** 2.92 M spans of history took 28 min through the OTLP path, with 35,338 spans (1.2%) reported failed. I did not
   investigate the failures; the correctness check balanced afterwards (27 of 27 cells, ledger counts the failures as not sent).
4. **Correctness** held in every run: all ledger cells matched, no drops, drain completed on shutdown.
5. **Browser tour:** 9 timeouts / 9 errors in E (above the ceiling), 6 / 3 in C; the repo's other providers show timeouts in the facet steps too,
   but I did not break these down.
6. **Collector log errors:** 576 in C, all readiness-check failures (finding 1) and 328 "export cancelled" warnings under the saturated gate.

## Not run

- **Two collectors on one root**, **kill -9 mid-ramp** and **compaction off/on**: the stress harness has no way to start a second collector or
  kill one, and compaction only matters over runs longer than its 10-minute minor age, which these were not. They need either harness
  support or a manual script; I did not build one. (Compaction off is `Telemetry__Parquet__Compaction__Enabled=false` via `--host-env`.)
- **PostgreSQL / ClickHouse comparison runs on this commit.** ClickHouse's recorded results (83,000 write-only, 42,700 with reads) were
  measured on the same machine on 2026-10-09 with a different tree; use them as context, not a controlled comparison.
- Export reads over history, and per-process CPU/memory.

## Decisions for you

- Raise the Parquet default gates (finding 1)? This is the one change that moves the headline number.
- Is 64,000 / 36,000 records/s enough, and is first-page `traces/list` at about 1.5 s under load acceptable?
- Do you want the missing multi-collector, crash and compaction runs, which need harness work first?

## Follow-up (2026-10-10)

Decision on the gates: **raised.** `AddParquetCollectorServices` post-configures the shared ingestion options: record gates default to
`2 x MaxBufferRecords` (1,000,000) and the byte gate to 2 GB, each only when its key is not in configuration. The shipped
collector `appsettings.json` set the three record gates explicitly (200000), which defeated the new default, so those three lines were
removed (run F, `p7-F-write-only-newdefaults`, still ran at 200,000 and tripped at x4). Run G (`p7-G-write-only-newdefaults`, default
config, no `--host-env`) reached **x21 (64,000 records/s)** and tripped at x22, as in run D. The 1.5 s first trace-list page under ramp load stays as is.

## Why reads cost writes (2026-10-10)

Process CPU from the hosts' metric files: in run E (reads) the API host used 1-5 cores (about 4.5 by x12) of the 8-core machine, which also ran
Chromium, the load generator and Docker; in run D (no reads) it used under 0.5. The collector used about the same in both (0.6-0.9 core
over x10-x12), so it was not doing more work, and the likely mechanism is CPU contention: the API's read database defaults to all
processors (`Telemetry:Parquet:DuckDb:Threads` unset).

Run H (`p7-H-ramp-threads2`) repeats run E with `Telemetry__Parquet__DuckDb__Threads=2` on both hosts:

| | E (all cores) | H (2 threads) |
|---|---|---|
| Last sustained step / records/s | x12 / about 36,000 | **x15 / about 46,000** |
| Commit lag p95 (whole run) | 49 s | 40 s |
| Marker probes timed out | 18 | 0 |
| `traces/list` p50 / p95 | 1,494 / 1,547 ms | 1,315 / 1,335 ms |
| `logs/list` p95, `traces/summary` p95 | 142, 54 ms | 134, 44 ms |
| Browser page-ready p95 | 1,441 ms | 2,316 ms |

So the cap recovers about a third of the gap to the write-only 64,000 without slowing the API routes (page-ready is browser-measured, with
the browsers competing for the same CPU, and rose). It confirms contention as a main cause but not the whole of it: the remaining gap may be
the same contention at 2 threads, disk I/O (not measured), or the load side. The 1.3 s `traces/list` under ramp load is not contention: it
did not move, so it is the query's own cost at that data volume. Not tried: pinning the API host (no support in the harness, no `taskset` on
macOS), a thread count of 1, and a separate machine for the hosts. **Option:** default `DuckDb:Threads` for the API's read database to a
fraction of the processors (for example half) instead of all of them; not changed.
