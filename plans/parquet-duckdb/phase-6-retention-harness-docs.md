# Phase 6: retention, stress harness and documentation

Part of the [Parquet + DuckDB provider](README.md).

## Decisions

| # | Decision |
|---|---|
| 1 | **`ParquetRetentionSweeper`** implements `IRetentionSweeper` directly: for each table, delete every `date=` folder whose whole day is before the cutoff (day granularity, U7, as ClickHouse), for every tenant folder present (data whose tenant was removed from the control plane still expires). The count returned is the raw tables' rows, summed from the manifests before deletion. Derived tables follow their signal's window (README D7) |
| 2 | A read whose file list included a day being deleted can fail with a missing file; retention deletes a day only after it has been out of every window for `GraceSeconds` (phase 4), so only a query reaching back past the retention window can see it. Accepted and documented |
| 3 | The collector already drops out-of-retention records at ingest (phase 2 decision 2), so a deleted day is not re-created by late data, except within the retention refresh interval; a re-created expired folder is deleted on the next sweep |
| 4 | **Stress harness**: a `Parquet` provider entry. There is no database container: the harness's database CPU/memory section reports the API host's and collector's processes instead and says so; `DatabaseOutage` does not apply; the correctness ledger counts rows through DuckDB over the manifests; `--seed-days` works through the real OTLP path as for other providers; slowest-SQL comes from the engine's own timing log (a `Debug` line per statement with elapsed ms and files read) |
| 5 | **Tests**: `RetentionTestsBase` on Parquet, mapping its table and column names to the Parquet ones as ClickHouse's test does, with the "hour before the cutoff" row not asserted (day granularity) |

## Documentation

- `src/Keryhe.Telemetry.Parquet/README.md`, the operator guide (README D10): when to choose it (single machine or a
  shared volume, modest volume, no database to run), the shared-disk requirement, configuration, sizing the gates for a
  30 s linger, what a crash loses, compaction and its grace period, backup (copy data files, then manifests), the
  attribute-as-string and metric-instance-per-service differences (as ClickHouse), and the measured limits from phase 7.
- `CLAUDE.md`: the provider in "Projects", "Provider abstraction" (a Parquet notes block like ClickHouse's), "Retention"
  table, "Database", the integration test filter values, the stress harness provider list.
- `docs/CONFIGURATION.md`: the `Telemetry:Parquet` section. `docs/SETUP.md` and the root `README.md`: the fifth
  provider. `schema/apply-schema.sh`: the `parquet` case (phase 1 decision 5).

## Done when

Retention passes its shared test on Parquet, the stress harness runs a Parquet smoke profile end to end, and the docs
above are updated.

## Implementation note (2026-10-10, uncommitted)

Done:
- **Retention**: `ParquetRetentionSweeper` already existed from phase 3 and `RetentionTestsBase` already ran on Parquet (plain and compacted
  fixtures); phase 6 only confirmed it (all retention tests pass). Differences from decision 2: the sweeper deletes a day folder at once, not
  after `GraceSeconds`; documented in the provider README and CLAUDE.md.
- **Stress harness**: `Parquet` is in `ProviderContainerFactory.ProviderNames` (so `--provider all` includes it). `HostLauncher` sets
  `Telemetry__Parquet__RootPath` (no connection strings) and turns `Keryhe.Telemetry.Parquet.ParquetQueryEngine` to Debug on the API host.
  `ParquetObserver` counts rows with DuckDB over the manifest-resolved files, samples file counts instead of locks, and parses the
  `parquet-sql ms= files= sql=` lines from the API host log (attached through `DatabaseObserverSession.AttachHostLog`); `CountRollupRowsAsync`
  became virtual for it. No container stats for Parquet (its `ContainerId` is the control plane's PostgreSQL).
- **Engine**: `NamedParameterConnection` logs each statement's elapsed ms and files read at Debug (`ParquetQueryEngine` logger).
- **Docs**: provider README (usage, when to choose, operating), CLAUDE.md (Parquet notes block, retention row, providers, filters), CONFIGURATION.md
  (`Telemetry:Parquet`), SETUP.md, root README, stress README.
- **Tests**: `ParquetObserverTests` (row counts per age against what was written, table rows, statement parsing); the whole `Provider=Parquet`
  integration suite passes (154).

Smoke run (`run --provider Parquet --profile smoke --browsers 0`): 18 of 18 ledger cells match, backdated rows removed by retention, no
host errors, ingest-to-queryable lag p50 about 15 s. Not done: a smoke run with the browser tour (Chromium), `--seed-days`, and the stress-harness
unit tests were not re-run; the DuckDB native libraries make the harness publish large.
