# Phase 1: the seam

Part of [Parquet on object storage](README.md). A refactor with **no behaviour change on local disk**: everything that touches
storage goes through `IParquetFileStore`, and `IParquetFileStore` is implemented once over `IObjectStore` (O17).

## What leaks today

Found by reading the provider (2026-10-10):

| Where | What | After |
|---|---|---|
| `ParquetWriterIdentity` | Holds `_writers/<id>` open with `FileShare.None` as a lock | An `IWriterIdentityProvider`: local keeps the lock; object stores per O8 (phase 4 adds the heartbeat) |
| `ParquetSchemaFile` | `File.Exists`/`File.Move(overwrite:false)` on `_schema.json` | `GetAsync` + `PutIfAbsentAsync` through the store; `Verify` reads through it |
| `ParquetServiceCollectionExtensions` | `Directory.Exists(RootPath)` on the API host; `Path.Combine(RootPath, "_tmp", ...)` for the writer's temp | `store.VerifyRootAsync()`; temp under `Storage:WorkPath` |
| `ParquetQueryEngine`, `ParquetCompactor` | DuckDB `temp_directory` defaults to `<RootPath>/_tmp/<host>` | Defaults to `Storage:WorkPath` (for `Local`, unchanged: `<RootPath>/_tmp/<host>`) |
| `ParquetCompactor.WriteCompactedAsync` | `COPY` to a temp directory, `Directory.GetFiles`/`Directory.Delete` on it | Unchanged in shape (it is local work), but the directory comes from a `LocalWorkArea`, not from `NewTempPath` under a partition |
| `IParquetFileStore.PathOf`, `ParquetResolvedFile.PathIn` | "The local path for DuckDB" | `ReadUriOf`; the name says it may not be a path |
| `IParquetFileStore.EnumeratePartitions` | Synchronous recursive directory walk | `EnumeratePartitionsAsync` (`IAsyncEnumerable`), delimiter listings (O18) |
| `IParquetFileStore.DeleteTempFiles`, `ListTempFilesAsync`, `DeleteTempFileAsync` | A `.tmp` folder inside each partition | Temp files live in the work area (O11). For `Local`, the work area stays inside each partition's `.tmp` (same volume, so the commit stays a rename); for object stores the temp listing of a partition is empty and startup cleans the work area |
| `ParquetCompactionLease` | Write then read back | Unchanged in phase 1; phase 4 makes it CAS |
| Tests and `ParquetObserver` | `new LocalParquetFileStore(root)`, `Directory.*` on the root | A factory that builds the configured store; tests that inspect files directly stay local-only and say so |

## Steps

1. `IObjectStore` and `LocalObjectStore` (README "The primitive layer"), with a unit suite every backend will run
   (`ObjectStoreContractTests`, abstract, one subclass per backend: put/get/ETag, `PutIfAbsent` race, `PutIfMatch` stale,
   paged list with delimiter, prefix delete, missing keys).
2. `ObjectParquetFileStore : IParquetFileStore` over it, moving the layout and manifest JSON out of `LocalParquetFileStore`, which is
   deleted. `CommitFileAsync(partition, localTempPath, finalName)` calls `PutFileAsync` and deletes the local file (for `Local`, a
   rename, as today).
3. The interface changes above: `ReadUriOf`, `EnumeratePartitionsAsync`, `VerifyRootAsync`, `ReadTextAsync`/`WriteTextAsync` gain
   ETag variants for phase 4. Callers updated: `ParquetFileResolver`, `ParquetCompactor`, `ParquetRetentionSweeper`,
   `ManifestWriter`, `ParquetManifests`, the tests' `FailingStore`.
4. `StorageOptions` (`Telemetry:Parquet:Storage`) with `Kind=Local` only accepted in this phase; `RootPath` is still the local root.
5. `IDuckDbStorageSetup` with a no-op local implementation, called wherever a DuckDB database is created (read engine, compactor,
   file writer, stress observer).

## Tests

All of `tests/Keryhe.Telemetry.Parquet.Tests` and `--filter "Provider=Parquet"` pass unchanged in what they assert (only
construction changes). `ObjectStoreContractTests` runs against `LocalObjectStore`.

## Done when

The suites pass, and a local `ramp-write-only` and `smoke` stress run on the same machine as phase 7 of the first plan are within
noise of its numbers (results recorded in this file's implementation note).
