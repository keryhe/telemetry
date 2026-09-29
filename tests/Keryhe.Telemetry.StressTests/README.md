# Keryhe.Telemetry.StressTests

A provider stress harness. For each database provider (PostgreSQL, Timescale, SqlServer, MySql, ClickHouse) and each host topology
(all-in-one, or split collector + API), it ingests a large volume of logs, traces and metrics over OTLP/gRPC while headless Chromium walks every
UI page, then reports what was slow, what locked, what it cost, and whether the rows in the database match what was sent.

It reports **numbers, not verdicts**: there are no pass/fail thresholds and no baseline comparison. The design and the decisions behind it are in
[`plans/stress-tests.md`](../../plans/stress-tests.md).

## Prerequisites

| Need | Why |
|---|---|
| Docker (Docker Desktop is fine) | Every scenario starts a fresh database container through Testcontainers. Give the Docker VM enough CPU and memory: each container is capped at 4 CPUs / 8 GB by default, and the report records the VM's size. |
| .NET 10 SDK | Builds the harness and publishes the hosts in Release. |
| Node.js | The hosts' publish builds the Angular UI. If Node is missing the publish only warns and packages an empty UI, and the harness then **fails fast** ("no index.html"). |
| Chromium for Playwright | Only for profiles with browsers. One-time, about 100 MB: `dotnet run --project tests/Keryhe.Telemetry.StressTests -- playwright-install`. Without it a scenario still runs, records `browserError`, and continues with no browsers. |

Everything runs on one machine (containers, hosts, load tool, Chromium), so absolute numbers belong to that machine. Compare providers on the same machine.

## Running

All commands start with `dotnet run --project tests/Keryhe.Telemetry.StressTests -- ...`.

```bash
# One scenario, the quick built-in profile (about 7 minutes)
dotnet run --project tests/Keryhe.Telemetry.StressTests -- run --provider PostgreSQL --topology allinone --profile smoke

# The whole matrix: every provider by both topologies, one profile (runs sequentially, never in parallel)
dotnet run --project tests/Keryhe.Telemetry.StressTests -- run --provider all --topology all --profile smoke

# Find a provider's breaking point
dotnet run --project tests/Keryhe.Telemetry.StressTests -- run --provider SqlServer --scenario ramp

# Rebuild the report from a finished run, without re-running anything
dotnet run --project tests/Keryhe.Telemetry.StressTests -- report --in stress-results/<timestamp>
```

`run` options:

| Option | Meaning |
|---|---|
| `--provider` | `PostgreSQL`, `Timescale`, `SqlServer`, `MySql`, `ClickHouse`, or `all` (default `PostgreSQL`) |
| `--topology` | `allinone`, `split`, or `all` (default `allinone`) |
| `--profile` | `smoke`, `standard`, `soak`, `ramp`, `all`, or the path of a profile JSON (default `smoke`, or `ramp` with `--scenario ramp`) |
| `--scenario` | `fixed` or `ramp`; picks the default profile and rejects a mismatched one |
| `--browsers <n>` | Override the profile's browser users (0 to 5; 0 runs none) |
| `--out <dir>` | Results folder (default `stress-results/<timestamp>/` at the repo root, which is gitignored) |
| `--reuse-publish <dir>` | Use hosts published by an earlier run instead of publishing again |
| `--retention-interval <v>` | Override the profile's retention interval: `stress` (30 s, a sweep lands in every window), `realistic` (3600 s, the API default: one sweep at host start, so retention contention does not dominate), or a number of seconds. The scenario name gets a `-ret<seconds>` suffix, so both kinds of run sit side by side in one comparison |

The hosts are published once per run. Ctrl-C stops after cleaning up the current scenario. A scenario that fails part-way still writes its result
(with the error) and the matrix carries on.

Smaller tools kept for development: `load` (the OTLP load tool alone, against a host you already started), `host-smoke` (one short end-to-end pass
with optional browsers), and `playwright-install`.

## What a scenario does

1. Starts a fresh database container with diagnostics on (pg_stat_statements, Query Store, deadlock logging, ...) and the CPU/memory cap, applies `schema/*.sql`, seeds tenants and API keys.
2. Launches the host(s) as child processes and attaches the database observers and an EventPipe metrics listener to each host.
3. **Warm-up**: ingestion only, so rollup coverage and data volume exist. Recorded, but excluded from the headline numbers.
4. **Measured window**: ingestion at the profile rate, the browser tour, and every observer together. Backdated records flow and the short retention interval makes sweeps land inside it. A **ramp** replaces this with rate steps and stops at the first step where a stop criterion holds for the whole step.
5. **Quiesce**: stops the load and waits until nothing is resident in the ingestion queue and nothing has been flushed for 10s (a timeout is recorded, not raised).
6. **Correctness check**: compares the sent ledger with per-tenant row counts. If backdated records were sent, it first waits for a retention sweep that *started after* quiescence (up to `2 x RetentionIntervalSeconds + 60s`, capped at `backdatedCheckMaxWaitSeconds`), since only then do leftover backdated rows mean anything. With a realistic retention interval no such sweep comes, and the backdated outcome is `NotVerifiable`.
7. Stops the observers, shuts the hosts down gracefully (recording whether the queue drained), saves logs, disposes the container.

## Built-in profiles

| Profile | Warm-up | Measured | Rate per signal | Browsers | Tenants |
|---|---|---|---|---|---|
| `smoke` | 1 min | 5 min | 500/s (spans, log records, data points) | 1 | 2 |
| `standard` | 5 min | 30 min | 5,000/s | 3 | 3 |
| `soak` | 15 min | 4 h | 5,000/s | 3 | 3 |
| `ramp` | 1 min | steps of 60s | x1 = 1,000/s, +1,000/s per step, up to 30 steps | 3 | 3 |

All four send 2% backdated records, 2% re-deliveries, 5% late arrivals and 2% orphan traces.

## Writing a profile

A profile JSON (comments allowed) lists only what it overrides. Pass its path to `--profile`. Files in `Profiles/` are the built-ins.

| Key | Default | Meaning |
|---|---|---|
| `name` | file name | Used in folder names and the report |
| `tenants` | 2 | Tenants seeded, each with one API key |
| `warmupSeconds` / `measuredSeconds` | 60 / 300 | `measuredSeconds` is ignored for a ramp |
| `retentionIntervalSeconds` | 30 | `Retention:IntervalSeconds` for the host, so a sweep lands in the window (see `--retention-interval`) |
| `backdatedCheckMaxWaitSeconds` | 300 | Longest the correctness check waits for a post-quiescence retention sweep |
| `containerCpus` / `containerMemoryGb` | 4 / 8 | The same cap for every DB container |
| `markerIntervalSeconds` | 5 | How often a marker log and span are sent to time ingest-to-queryable lag |
| `quiesceStableSeconds` / `quiesceTimeoutSeconds` | 10 / 180 | |
| `browsers.users` | 1 | 0 to 5; each user is pinned to one tenant round-robin |
| `browsers.thinkTimeSeconds` / `readyTimeoutSeconds` | 1 / 60 | |
| `browsers.windows` | `["1h","6h","24h","7d"]` | UI time-range presets, rotated one per tour loop |
| `browsers.export` | false | Adds one export download per signal to the tour |
| `load.seed` | 1 | Same seed, same data shape and rate schedule (timestamps differ) |
| `load.traces.spansPerSecond`, `load.logs.recordsPerSecond`, `load.metrics.dataPointsPerSecond` | 500 | 0 disables a signal |
| `load.transport` | 100 records/export, 2 channels | Also `connectionsPerChannel`, `maxInFlightExports`, `exportTimeoutSeconds` |
| `load.time` | all 0 | `lateArrivalFraction`, `orphanFraction`, `backdatedFraction` (+ `backdatedAgeDays`), `redeliveryFraction` |
| `load.servicesPerTenant`, `load.operationsPerService`, the rest of `load.traces/logs/metrics` | see `Load/LoadProfile.cs` | Cardinality, attribute counts, severity mix, metric type mix, exemplar and delta fractions |
| `ramp` | absent | Present means a ramp. `startScale`, `stepScale`, `stepSeconds`, `maxSteps`, and `criteria` (below) |

Ramp criteria (each applies to a whole step; the rate scale multiplies the profile's load rates):

| Criterion | Default | Trips when |
|---|---|---|
| `maxDroppedRecords` | 0 | More records were dropped in the step |
| `maxGateWaitP95Ms` | 250 | The worst signal's gate-wait p95 is higher (the gate is saturated and clients are being held) |
| `maxExportP99Seconds` | 5 | Any signal's client-side Export p99 is higher |
| `maxErrorRatePercent` | 1 | More than this percentage of a signal's exports failed with a gRPC error |
| `lagGrowthFactor` / `lagGrowthMinMs` | 2 / 3000 | `lag_growth`: lag in the last third of the step's probes is that many times the first third's and at least that much higher, or a probe never appeared |
| `maxLagSeconds` | 10 | `lag_absolute`: lag in the last third of the step's probes averages more than this, growing or not (0 disables) |

Both lag criteria judge the log lag **after subtracting the provider's `asOf` pin offset**. The log probe reads through the pinned list page, and
PostgreSQL/Timescale pin `asOf` at `NOW() - 5 s` by design, so their log lag has a constant 5 s floor that would otherwise shrink the growth
ratio and count against the absolute limit. The offset is read at scenario start from `GET /api/capabilities` (`asOfBackoffSeconds`), not
hard-coded here, and recorded as `logPinOffsetMs` in `scenario.json`. The trace probe (`api/traces/{id}/spans`) is not pinned. A probe still
polling when its step ends counts as a lower-bound lag once it has already been waiting longer than `maxLagSeconds`, so the criteria are not
blind exactly when lag is worst.

The report names the last step that sustained and which criterion tripped. That is a stop rule for finding the breaking point, not a verdict.

## Output

```
stress-results/<timestamp>/
  run.json  result.json  report.html  comparison.html  matrix.json     comparison.html only for a matrix
  <provider>-<topology>-<profile>/
    scenario.json                       everything the scenario measured
    host-<role>.log                     each host's console output
    host-<role>-metrics.ndjson          each host's EventPipe samples (1s)
    container.log                       the database container's log
    sqlserver-deadlock-N.xdl            deadlock graphs (SQL Server), open in SSMS
    screenshots/                        pages that timed out or errored
  publish/                              the published hosts (unless --reuse-publish)
```

`result.json` is the full `RunResult` (`schemaVersion` 2; version-1 ramp results used the older lag rule and are not comparable): run metadata (git SHA, machine, .NET, Docker VM size, images) and, per scenario, its
result, every time series, and the summary tables. Use it to compare runs by hand or with tooling.

### Reading `report.html`

- **Summary** cards and, for a ramp, the step table and breaking point. **Correctness** lists mismatched cells (table, tenant, expected, actual, delta); `ExplainedByDrops` means the shortfall is no bigger than `records_dropped`; `ExplainedByAbandonedExports` means a surplus no bigger than the rows of exports the client gave up on (deadline exceeded, cancelled, or cut off when the load stopped), which the server may have enqueued anyway; and a backdated outcome of `NotVerifiable` means no sweep started after quiescence so leftover rows prove nothing.
- **Timelines** share one x-axis (time since warm-up began). Grey lines mark phase boundaries, orange dashed lines retention sweeps, blue dotted lines ramp steps; hover a line for its label. Line up a latency spike with a lock burst or a sweep by eye.
- **Write side**: client Export latency, server gate wait / flush duration / batch size, retries and drops, ingest-to-queryable lag. Log lag sits near 5s on PostgreSQL and Timescale because list pages pin their query `asOf` to `NOW() - 5s`; trace lag is not pinned. The ramp step table shows the log lag with that offset removed.
- **Read side**: per-page time to ready, and per endpoint the browser's timing next to the host's. Server percentiles are the count-weighted mean of per-second quantiles, so they are approximate, and the server count includes every caller of the route (including the marker probe). A `400` on a search is the standard tier's documented answer outside its raw-search window, not an error.
- **Database**: blocking chains, deadlocks, top statements by total and mean time, table sizes, then the provider's own diagnostics and the container's **effective server settings** (memory, WAL/redo, durability, isolation), so a comparison between providers can be checked for fairness. Diagnostics: PostgreSQL/Timescale foreign-key `FOR KEY SHARE` checks (`pg_stat_statements.track = all`, so checks fired inside `COPY` count), checkpoint/WAL deltas and "checkpoints are occurring too frequently" warnings, dead tuples and autovacuum per table, and on Timescale every policy job with its failures and errors; SQL Server `spans` index usage and operational stats, autogrowth events for the database and tempdb, file sizes; ClickHouse rows/bytes read per query shape, `part_log` merges and mutations, the mutation list, merges still running; MySQL buffer-pool hit ratio, redo and purge (history list length is also sampled every second), per-index I/O on `spans`, unused indexes. A section that could not be collected shows its error instead of failing the run. The "API reads run under SNAPSHOT" check for SQL Server reads `not checked` when no read request happened to be sampled (for example a run without browsers); that is unknown, not a failure.

## Things worth knowing

- The metrics catalog only lists a metric once `MetricTouchWorker` has flushed (60s by default), so the browser tour waits up to 90s at start for it. Use a warm-up of at least 70s with browsers.
- The tour opens metric detail by clicking through the metrics list, not by URL, because a hard load of `/metrics/<dotted.name>` is answered 404 by the UI host.
- ClickHouse deletes are asynchronous mutations, so after a sweep the correctness check re-counts for up to 60s before reporting leftover backdated rows.
- There is no product endpoint to trigger a retention sweep (by design); the harness waits for a natural one.
- The load tool is open-loop. If it falls behind its own schedule it reports `not sent` and skipped exports, so load-tool saturation is never mistaken for server slowness; check offered versus acked.

## Tests

The harness's pure logic (profiles, CLI matrix, ramp rules, quiescence, correctness comparison, chart maths, report analysis) and its per-provider database observers are covered by
`tests/Keryhe.Telemetry.IntegrationTests` (`ScenarioTests`, `CorrectnessTests`, `BrowserTourTests`, `ReportTests`, `DatabaseObserverTests`, `HostOrchestrationTests`, `OtlpLoadToolTests`).
The observer tests need Docker. None of this runs a stress scenario; that is always manual (`run`).
