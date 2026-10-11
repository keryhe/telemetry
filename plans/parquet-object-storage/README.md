# Parquet provider on object storage (S3, Azure Blob, local)

Written 2026-10-10. Follows the [Parquet + DuckDB provider](../parquet-duckdb/README.md), whose U4 deferred object storage to
"a later phase behind a storage abstraction introduced now" (`IParquetFileStore`). This plan makes the Parquet provider run on
**AWS S3 (and S3-compatible stores)**, **Azure Blob Storage** and **local disk**, chosen by configuration, with the same layout,
manifests and compaction on all three.

The point of it: collectors and API hosts on **different machines** sharing one root, which v1 rules out (a network share gave
stale manifests). S3 and Azure Blob are strongly consistent for read-after-write and list and have conditional writes, which is
everything the manifest protocol needs from a file system and the shares did not provide.

## Status

Planned 2026-10-10. Not started.

## User decisions (2026-10-10)

The numbers are the questions asked before the plan; "default" means the user accepted the proposed default.

| # | Decision |
|---|---|
| O1 | **Goal: multi-collector on cloud storage**, read latency close to local for a 24 h window (decision O7 makes "close" a number) |
| O2 | **v1 ships S3 (with an endpoint override, so MinIO/R2 and other S3-compatible stores work) and Azure Blob (flat namespace).** ADLS Gen2's hierarchical namespace is not special-cased: it works as a flat account |
| O3 | **Local disk stays first-class**: it remains the default and keeps its tests and its phase 7 numbers as the regression baseline |
| O4 | **Both hosts use the same store and the same storage configuration**; no mixed setups |
| O5 | **DuckDB reads remote objects directly** (`httpfs` for S3, `azure` for Blob) in v1. The interface hands DuckDB a *read URI*, so a local read-through cache can be added behind it later without changing callers |
| O6 | **DuckDB extensions** are auto-installed by default, with a switch for an offline extension directory (`Storage:DuckDb:ExtensionDirectory`, `AutoInstallExtensions=false`) |
| O7 | **Latency target** (proposed by this plan, see [phase 6](phase-6-measurement.md)): with 25 ms injected round-trip time, trace list and trace detail p95 at most local p95 + 150 ms; 24 h dashboard summary at most 1 s p95 |
| O8 | **Writer identity without file locks**: on object storage a collector's id is its configured `WriterId` used as is (the operator keeps it unique; Kubernetes StatefulSet pod names do), otherwise machine name + a random suffix **per process start**, never reused. The compactor retires dead writers' manifests (phase 4). Local keeps its lock-file identity |
| O9 | **One compaction-lease design for all three backends**: compare-and-swap on the lease object's ETag, with expiry. No Azure native leases |
| O10 | **Commit latency**: per-backend default overrides (linger, buffer sizes, flush concurrency) if measurement asks for them; no structural change to ingestion |
| O11 | **Temp files are local** on every backend (a local work directory); there is no remote `.tmp`. Abandoned multipart uploads are cleaned by a bucket lifecycle rule the README documents, not by code |
| O12 | **Credentials**: the platforms' default chains (AWS SDK credential chain; `DefaultAzureCredential`), plus optional explicit credentials (access key / connection string / SAS) from user secrets or environment, never appsettings |
| O13 | **Retention stays the sweeper** on every backend (it follows the control plane's windows). Lifecycle rules only for multipart and stray-object cleanup |
| O14 | **Encryption, policy and versioning are the bucket's.** The README says versioning should be off, or on with a noncurrent-version expiry (otherwise every delete keeps paying) |
| O15 | **The layout is unchanged**, under a configurable key prefix, so a root moves between backends by copying it |
| O16 | **Moving between backends** is documented (copy the data files, then the manifests, then switch configuration); no tooling |
| O17 | **A small object-store primitive layer** under one shared `IParquetFileStore` implementation; local, S3 and Azure each implement only the primitives (phase 1) |
| O18 | **Partition enumeration becomes asynchronous** and uses delimiter listings, computing hours from the time range where it can |
| O19 | **Emulators only**: MinIO and Azurite through Testcontainers, in the integration suite as their own provider names. **No real-cloud run.** |
| O20 | **Stress harness** runs Parquet on MinIO and Azurite (`--storage s3|azure`), with Toxiproxy in front for latency |
| O21 | **The cost model comes from measurement**: the store counts requests per operation and the harness reports them per signal and per tenant-hour; it is not a gate before implementation |

### What "emulators only" leaves unproven

Said once here so the results do not overclaim:
- **Credential chains** (IAM role, instance profile, workload identity, managed identity) are never exercised; emulators use
  explicit keys. The code path is a single SDK call per backend, and the README tells operators how to check it.
- **Real latency and throughput.** Toxiproxy adds round-trip time but not S3's per-prefix request limits, throttling (`503 SlowDown`,
  Azure `503 ServerBusy`) or long-tail latency. Throttling is covered by tests with a fault-injecting store, not by a real service.
- **Emulator conditional-write fidelity**: phase 0 verifies MinIO and Azurite honour `If-None-Match`/`If-Match`; AWS and Azure
  document the same behaviour, but it is taken on their word.

## What changes, in one picture

```
                    today                                           after
ParquetBulkWriter ─┐                                ParquetBulkWriter ─┐
ParquetCompactor  ─┼─ IParquetFileStore ─ Local     ParquetCompactor  ─┼─ IParquetFileStore ── ObjectParquetFileStore ─ IObjectStore ─┬─ LocalObjectStore
ParquetFileResolver┘   (paths, rename, dirs)        ParquetFileResolver┘   (keys, read URIs)     (layout, manifests,                    ├─ S3ObjectStore
ParquetWriterIdentity ─ file lock                                                                  temp→upload)                         └─ AzureBlobObjectStore
ParquetSchemaFile    ─ File.*                        writer identity, schema file, lease, root check ─ through the store
DuckDB ─ local paths                                 DuckDB ─ read URIs + IDuckDbStorageSetup (secrets, extensions)
```

## Design

### The primitive layer (`IObjectStore`)

Keys are `/`-separated, relative to the configured prefix. Every operation is async and cancellable.

| Primitive | Local | S3 | Azure Blob |
|---|---|---|---|
| `GetAsync(key)` → bytes + ETag, or null | read file; ETag = SHA-256 of content (only small objects are read this way) | `GetObject` | `DownloadContent` |
| `PutAsync(key, bytes)` → ETag | temp + rename (overwrite) | `PutObject` | `Upload(overwrite: true)` |
| `PutFileAsync(key, localPath)` | rename into place (same volume) or copy | `TransferUtility` (multipart over `MultipartThresholdBytes`) | `Upload` with `StorageTransferOptions` |
| `PutIfAbsentAsync(key, bytes)` → bool | temp + rename without overwrite | `If-None-Match: *` (412 → false) | `IfNoneMatch = ETag.All` (409 → false) |
| `PutIfMatchAsync(key, bytes, etag)` → new ETag or null | exclusive `.lock` file, compare hash, rename | `If-Match` (412 → null) | `IfMatch` (412 → null) |
| `ListAsync(prefix, delimiter?)` → objects (name, size, modified) and common prefixes | directory enumeration | `ListObjectsV2` (paged) | `GetBlobsByHierarchy` (paged) |
| `DeleteAsync(key)` | `File.Delete` | `DeleteObject` | `DeleteIfExists` |
| `DeletePrefixAsync(prefix)` → count | `Directory.Delete(recursive)` | list + `DeleteObjects` in 1,000-key batches | list + batch delete (256 per batch) |
| `ReadUri(key)` | absolute path | `s3://bucket/prefix/key` | `az://container/prefix/key` |

`ObjectParquetFileStore` implements today's `IParquetFileStore` contract (renamed where it says "path") on top: the partition
layout, manifest JSON, `.tmp` work files, generation publish. The local behaviour must not change; phase 1 proves it with the
existing suites and a local stress smoke.

### Correctness on object storage

The manifest protocol needs four guarantees; all four hold on S3 and Azure Blob, and phase 0 checks them on the emulators:

1. **A file is visible only once a manifest lists it.** Upload the data object, then write the manifest. A crash between them
   leaves an unlisted object (the orphan sweep removes it), as today.
2. **A writer's manifest is replaced whole.** A plain `PUT` is atomic for readers (they see the old or the new object, never a mix).
   Only one writer owns a manifest; the write still uses `If-Match` on the ETag it last wrote, so a second process with the same
   `WriterId` fails loudly instead of silently losing entries (O8).
3. **A generation is published once**: `PutIfAbsent`. Exactly one of two racing compactors wins, as with rename-without-overwrite.
4. **Read order**: the writers' manifests are read before the generation is listed (phase 0 spike 3 of the first plan). Strong
   read-after-write and list consistency keep this valid; the stale-manifest failure seen on network shares cannot happen.

New invariant (phase 4): **a writer's next sequence in a partition is the larger of its manifest's `NextSequence` and every
generation's `through` for it, plus one.** Today the manifest alone carries it, so a deleted manifest (retirement, or an operator
wiping a writer) would restart the sequence at 1 and the generation's `through` would hide the new files. Today's ids are reused
across restarts, so it does not arise; with retirement it would.

### Reads

`ParquetFileResolver` lists partitions on every request today (`EnumeratePartitions` walks directories, then
`ListManifestNamesAsync` per hour, then a GET per manifest). On local disk that is microseconds; on S3 each step is a round trip.
Phase 5 adds:
- Partition discovery from the time range: one delimiter list of a tenant's `date=` prefixes in range, one of a day's `hour=` prefixes,
  instead of a recursive walk.
- A **manifest cache**: generation manifests are immutable by name (published once, deleted only after the grace), so they are
  cached until deleted; writer manifests and listings are cached for `Storage:ManifestCacheSeconds` (default 5 s, against a 30 s
  linger it barely moves freshness). Requests within a partition run concurrently, bounded by `Storage:MaxConcurrentRequests`.
- DuckDB's own HTTP metadata and file caches turned on (`enable_http_metadata_cache`; the external file cache where the bundled
  DuckDB version has it).
- Every store call counted on the `Keryhe.Telemetry.Parquet.Storage` meter (`storage_requests{op, backend, outcome}`,
  `storage_request_duration`, `storage_bytes{direction}`), which is how O21's cost model is built.

### DuckDB

`IDuckDbStorageSetup`, one per backend, gives the SQL each new DuckDB database runs: `LOAD` the extension (or `INSTALL` then `LOAD`
when auto-install is on), `SET extension_directory`, and `CREATE SECRET` (S3: `PROVIDER credential_chain`, or `KEY_ID`/`SECRET`,
plus `ENDPOINT`/`URL_STYLE 'path'`/`USE_SSL` for MinIO; Azure: `PROVIDER credential_chain`, or `CONNECTION_STRING`). The read engine,
the compactor's database and the stress observer all go through it. The .NET SDK and DuckDB resolve credentials separately;
phase 0 checks that one configuration feeds both.

Writes never go through DuckDB to the store: DuckDB `COPY`s to the local work directory and `PutFileAsync` uploads, so Azure's
write support in the extension does not matter and every write takes the same conditional, counted path.

### Configuration (`Telemetry:Parquet:Storage`)

| Key | Default | Meaning |
|---|---|---|
| `Kind` | `Local` | `Local`, `S3` or `AzureBlob` |
| `Prefix` | (empty) | Key prefix inside the bucket/container (the root) |
| `WorkPath` | `<system temp>/keryhe-parquet/<host>` | Local work directory: temp Parquet files, DuckDB spill. `RootPath` stays the root for `Local` |
| `S3:Bucket`, `S3:Region`, `S3:ServiceUrl`, `S3:ForcePathStyle` | (required), SDK default, AWS, `false` | `ServiceUrl` + `ForcePathStyle=true` is MinIO and friends |
| `S3:AccessKeyId`, `S3:SecretAccessKey` | (none: credential chain) | Secrets/environment only |
| `Azure:ServiceUri`, `Azure:Container` | (required) | `https://<account>.blob.core.windows.net` |
| `Azure:ConnectionString` | (none: `DefaultAzureCredential`) | Secrets/environment only; Azurite uses it |
| `MultipartThresholdBytes` | 64 MiB | Above it an upload is multipart/blocks |
| `ManifestCacheSeconds` | 5 | Writer-manifest and listing cache |
| `MaxConcurrentRequests` | 32 | Per host |
| `DuckDb:AutoInstallExtensions`, `DuckDb:ExtensionDirectory` | `true`, DuckDB's default | Offline deployments set both |

`Validate()` fails startup on a missing bucket/container, an `S3:ServiceUrl` without a scheme, `S3:AccessKeyId` without
`S3:SecretAccessKey` (or the reverse), or a `Kind` other than the three. Where credentials come from is not checked: the README
says secrets or environment only.

## Plans, in order

| Phase | Plan | Items | Size |
|---|---|---|---|
| 0 | [phase-0-spikes.md](phase-0-spikes.md) | Emulator conditional writes; DuckDB `httpfs`/`azure` against MinIO/Azurite; offline extensions; resolve cost with injected latency | Small. Throwaway code, one results file |
| 1 | [phase-1-seam.md](phase-1-seam.md) | `IObjectStore`, `ObjectParquetFileStore`, `LocalObjectStore`; identity, schema file, lease, root check and temp paths behind the store; async enumeration; read URIs | Large. No behaviour change on local |
| 2 | [phase-2-s3.md](phase-2-s3.md) | `S3ObjectStore`, DuckDB S3 setup, options; MinIO fixture runs the shared suites (`Provider=ParquetS3`) | Medium |
| 3 | [phase-3-azure.md](phase-3-azure.md) | `AzureBlobObjectStore`, DuckDB Azure setup; Azurite fixture (`Provider=ParquetAzure`) | Medium |
| 4 | [phase-4-writers-and-compaction.md](phase-4-writers-and-compaction.md) | Writer ids and heartbeats, sequence invariant, manifest retirement, CAS lease, compactor I/O over read URIs, orphan sweep by listing | Medium |
| 5 | [phase-5-read-path-and-ops.md](phase-5-read-path-and-ops.md) | Manifest cache, concurrent resolution, DuckDB caches, storage meter; stress harness `--storage`, observer; docs | Medium to large |
| 6 | [phase-6-measurement.md](phase-6-measurement.md) | Contention, request counts and cost model, latency under Toxiproxy, local regression | Medium. Runs, not code |

Why this order:
- Phase 0 can change the design: if an emulator does not honour a conditional header, its tests need a different oracle, and if
  DuckDB cannot read Azurite the Azure fixture needs another route.
- Phase 1 is a refactor proven on local disk alone, before any cloud code exists, so a regression is attributable.
- S3 before Azure: MinIO is the more complete emulator, and the second backend then only fills in primitives.
- Phase 4 after both backends: the writer and lease changes are backend-neutral but only matter, and are only testable for
  contention, once two processes can share a remote root.
- Phase 5's caches are tuned by request counts, which need the meter and a backend.

## Risks

| Risk | Mitigation |
|---|---|
| Object-store reads are far slower per file than local, and the resolver issues several requests per partition | Phase 0 spike 4 measures it before anything is built; phase 5's cache; compaction already keeps a closed day to a few files |
| A commit is about five requests (GET manifest, PUT file, LIST + GET generation, PUT manifest), so commit latency limits throughput | Measured in phase 6; flush concurrency and buffer sizes per backend (O10); the generation read could later move to the cache |
| PUT cost on a many-tenant deployment | O21's counts per tenant-hour; compaction and linger are the levers; the README gives the arithmetic |
| A long export reads a file the compactor deleted after the grace | Unchanged from local (documented); object stores add nothing |
| Emulator behaviour differs from the real service | Listed above under "What emulators only leaves unproven" |
| DuckDB extension and DuckDB.NET versions must match (`DuckDB.NET.Data.Full` 1.5.5) | Phase 0 spike 3 pins the extension version; the README says how to fetch extensions for an offline install |

## Out of scope

- A local read-through cache of data files (O5 leaves room for it).
- Google Cloud Storage (its S3-interoperability mode may work through `S3:ServiceUrl`; not tested).
- Real-cloud runs (O19), cross-region setups, a write-ahead log, schema changes.
- Querying the bucket from outside the API (the layout is still not a public contract).
