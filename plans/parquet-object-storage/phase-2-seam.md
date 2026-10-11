# Phase 2: the storage seam

Part of [Parquet on object storage](README.md). A refactor with **no behaviour change on local disk**: everything that touches
storage goes through `IParquetFileStore`, implemented once over `IObjectStore` (O17). After phase 1 the store holds only data
files, so this is smaller than the first version's seam: no manifests, no text files, no conditional operations.

## What leaks after phase 1

| Where | What | After |
|---|---|---|
| `ParquetServiceCollectionExtensions` | `Directory.Exists(RootPath)` on the API host; `Path.Combine(RootPath, "_tmp", ...)` for the writer's temp | The root check is the catalog's `VerifyRootAsync` (phase 1); temp under `Storage:WorkPath` |
| `ParquetQueryEngine`, `ParquetCompactor` | DuckDB `temp_directory` defaults to `<RootPath>/_tmp/<host>` | Defaults to the process's work-area subdirectory (for `Local`, under `<RootPath>/_tmp`, as today) |
| `ParquetCompactor.WriteCompactedAsync` | `COPY` to a temp directory, `Directory.GetFiles`/`Directory.Delete` on it | Unchanged in shape (it is local work), but the directory comes from the work area, not from `NewTempPath` under a partition |
| `IParquetFileStore.PathOf`, `ParquetResolvedFile.PathIn` | "The local path for DuckDB" | `ReadUriOf`; the name says it may not be a path |
| `IParquetFileStore.DeleteTempFiles`, `ListTempFilesAsync`, `DeleteTempFileAsync` | A `.tmp` folder inside each partition; startup deletes files matching this writer's id | For `Local`, a collector's data temp files stay inside each partition's `.tmp` (same volume, so the move stays a rename), cleaned by age as the compactor does today (phase 1's writer ids are per start, so matching by id would leak). Everything else, and all temp files on object stores, live in the process's own **work-area subdirectory** `<host>-<role>-<start id>` holding an `.owner` lock file open with `FileShare.None`; startup deletes sibling subdirectories whose lock no process holds (R11; the work area is always local disk, where that lock works) |
| The safety-net sweep's listing (phase 1 decision 10) | `Directory.EnumerateFiles` per partition | `ListAsync(prefix, "/")`, only every `BucketSweepDays` |
| Clean-up of abandoned staged objects, superseded files | `File.Delete` | `DeleteAsync` / `DeleteManyAsync` |
| Retention's day delete | Phase 1: the expired keys, deleted one by one | `DeleteManyAsync` of the keys `ExpireDayAsync` returned (C16) |
| Tests and `ParquetObserver` | `new LocalParquetFileStore(root)`, `Directory.*` on the root | A factory that builds the configured store; tests that inspect files directly stay local-only and say so |

## Steps

The order keeps the build green: the seam is built beside `LocalParquetFileStore`, callers move to it, then the old store is
deleted.

1. `IObjectStore` (README "The primitive layer") in `Keryhe.Telemetry.Parquet`, public, with a contract suite every backend runs
   (`ObjectStoreContractTests`, abstract: put file, get file, list with and without delimiter and paging, delete many with its
   count and missing keys among them, `ExistsAsync`, missing keys, keys with `=` and `_`). The abstract class and its Local subclass live in the no-Docker
   `Keryhe.Telemetry.Parquet.Tests`; phases 3 and 4 put the MinIO and Azurite subclasses in `IntegrationTests` (R14). Decide here
   how `IntegrationTests` reaches the base class (project reference or a linked source file).
2. **The `Keryhe.Telemetry.Parquet.Local` project** (O22): packable like the other providers (version 2.0.0), referencing only
   `Keryhe.Telemetry.Parquet`. It takes `LocalObjectStore`, the no-op DuckDB setup and the per-partition `.tmp` handling, and
   exposes `AddParquetLocalStorage(configuration)`.
3. **Backend registration in the core**: `IParquetStorageBackend { Kind }`, `IStorageErrorClassifier` (consulted by
   `ParquetFlushErrorClassifier`; answers transient, unavailable (C6) or permanent; Local's maps a full or missing volume to
   unavailable and nothing else new) and the startup validator's backend checks (none, several, or a
   `Kind` mismatch fails, naming the `AddParquet...Storage` call). Both hosts get the `Kind` switch with `Local` only, and reference
   the Local project; `TestInfrastructure`, `Keryhe.Telemetry.Parquet.Tests` and `StressTests` reference it too. `Telemetry.sln`
   gains the project.
4. `ObjectParquetFileStore : IParquetFileStore` over `IObjectStore` (in the core), taking the layout from `LocalParquetFileStore`.
   `CommitFileAsync(partition, localTempPath, finalName)` calls `PutFileAsync` and deletes the local file (for `Local`, a rename,
   as today). Callers updated: `ParquetBulkWriter`, `ParquetFileResolver`, `ParquetCompactor`, `ParquetRetentionSweeper`, the
   tests' `FailingStore`. **Then** `LocalParquetFileStore` is deleted, and `Keryhe.Telemetry.Parquet` contains no file-system
   storage code (the work area aside).
5. `StorageOptions` (`Telemetry:Parquet:Storage`, the shared keys) with `Kind=Local` only accepted in this phase; `RootPath` is
   still the local root, read by the Local package; `WorkPath` and its per-process subdirectory (README "Configuration"). The
   catalog root row's `storage_kind`/`storage_location` now come from the backend.
6. `IDuckDbStorageSetup` in the core, with the Local package's no-op implementation, called wherever a DuckDB database is created
   (read engine, compactor, file writer, stress observer). Its shape allows a refresh (phases 3 and 4, R8): the engine asks it for
   the setup SQL at creation and lets it re-run statements later.
7. **Per-backend defaults** (C19, C20, C22; phase 1 decision 12): applied from the registered `Kind` before options validation,
   only where the key is not configured. Local's are today's values, so nothing changes on Local; a unit test per key checks
   both kinds and that a configured value wins.

## Tests

All of `tests/Keryhe.Telemetry.Parquet.Tests` and `--filter "Provider=Parquet"` pass unchanged in what they assert (only
construction changes). `ObjectStoreContractTests` runs against `LocalObjectStore`. A unit test per validator case: no backend
registered, two registered, `Kind` mismatch. A work-area test: a held and an abandoned subdirectory side by side; a new start
removes only the abandoned one.

## Done when

The suites pass, and a local `ramp-write-only` and `smoke` stress run are within noise of phase 1's numbers (results recorded in
this file's implementation note).
