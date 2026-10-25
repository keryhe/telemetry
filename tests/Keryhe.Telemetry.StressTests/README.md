# Keryhe.Telemetry.StressTests

A manual load harness (never part of `dotnet test`). For each provider it starts a fresh database, runs the collector and API hosts,
sends OTLP logs, traces and metrics while headless Chromium walks the UI, and writes a report of latency, locking, resource use and
whether the stored rows match what was sent. Everything runs on one machine, so compare providers on the same machine.

## Prerequisites

| Need | Why |
|---|---|
| Docker (Docker Desktop is fine) | Every scenario starts a fresh database container through Testcontainers. Give the Docker VM enough CPU and memory: each container is capped at 4 CPUs / 8 GB by default, and the report records the VM's size. |
| .NET 10 SDK | Builds the harness and publishes the hosts in Release. |
| Node.js | The hosts' publish builds the Angular UI. If Node is missing the publish only warns and packages an empty UI, and the harness then **fails fast** ("no index.html"). |
| Chromium for Playwright | Only for profiles with browsers. One-time, about 100 MB: `dotnet run --project tests/Keryhe.Telemetry.StressTests -- playwright-install`. Without it a scenario still runs, records `browserError`, and continues with no browsers. |

## Running

All commands start with `dotnet run --project tests/Keryhe.Telemetry.StressTests -- ...`.

```bash
# One scenario, the quick built-in profile (about 7 minutes)
dotnet run --project tests/Keryhe.Telemetry.StressTests -- run --provider PostgreSQL --topology split --profile smoke

# The whole matrix: every provider, one profile (runs sequentially, never in parallel)
dotnet run --project tests/Keryhe.Telemetry.StressTests -- run --provider all --topology all --profile smoke

# Find a provider's breaking point
dotnet run --project tests/Keryhe.Telemetry.StressTests -- run --provider SqlServer --scenario ramp

# The same ramp with nothing reading the database (no browsers, no marker probes): the provider's write ceiling.
# Run it and "ramp" on the same commit; the difference between the two ceilings is what reads cost the write path.
dotnet run --project tests/Keryhe.Telemetry.StressTests -- run --provider SqlServer --topology split --profile ramp-write-only

# Rebuild the report from a finished run, without re-running anything
dotnet run --project tests/Keryhe.Telemetry.StressTests -- report --in stress-results/<timestamp>
```

`run` options:

| Option | Meaning |
|---|---|
| `--provider` | `PostgreSQL`, `SqlServer`, `MySql`, `ClickHouse`, `Parquet`, or `all` (default `PostgreSQL`) |
| `--topology` | `split` or `all` (default `split`). The collector and API run as separate hosts; `split` is the only topology, and `all` selects it |
| `--profile` | `smoke`, `standard`, `soak`, `ramp`, `all`, or the path of a profile JSON (default `smoke`, or `ramp` with `--scenario ramp`) |
| `--scenario` | `fixed` or `ramp`; picks the default profile and rejects a mismatched one |
| `--browsers <n>` | Override the profile's browser users (0 to 5; 0 runs none) |
| `--out <dir>` | Results folder (default `stress-results/<timestamp>/` at the repo root, which is gitignored) |
| `--reuse-publish <dir>` | Use hosts published by an earlier run instead of publishing again |
| `--control-plane <v>` | Where the control plane runs: `sqlite` (a file in the scenario folder) or `postgresql` (a PostgreSQL container, for ClickHouse and Parquet, or PostgreSQL's own database). Defaults: Parquet `sqlite`, ClickHouse `postgresql`, the relational providers their own database; `sqlite` works for every provider, `postgresql` is refused for SQL Server and MySQL. One value applies to every provider of the run. A choice that moves a provider off its default tags the scenario (`-cpsqlite`, `-cppostgresql`), so it gets its own folder and never reads as the default scenario. |
| `--db-cpuset <cpus>` | Pin the database container to these CPUs of the Docker VM (Docker `--cpuset-cpus`, e.g. `0-3`). The report states what the database shared CPUs with; under Docker Desktop the VM still shares the host's cores, so a database on another machine is the only fully separate option (not supported by the harness yet) |
| `--retention-interval <v>` | Override the profile's retention interval: `stress` (30 s, a sweep lands in every window), `realistic` (3600 s, the API default: one sweep at host start, so retention contention does not dominate), or a number of seconds. The scenario name gets a `-ret<seconds>` suffix, so both kinds of run sit side by side in one comparison |

The hosts are published once per run. Ctrl-C stops after cleaning up the current scenario. A scenario that fails part-way still writes its result
(with the error) and the matrix carries on.

Smaller tools kept for development: `load` (the OTLP load tool alone, against a host you already started), `host-smoke` (one short end-to-end pass
with optional browsers), and `playwright-install`.

## More

[Stress tests](../../docs/STRESS-TESTS.md): what a scenario does, the built-in profiles and ramp criteria, writing a profile, the output
files and how to read the report.
