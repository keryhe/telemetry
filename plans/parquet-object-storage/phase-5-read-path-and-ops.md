# Phase 5: read path, harness and documentation

Part of [Parquet on object storage](README.md). Phase 1 already moved file lookup to the catalog (with its 5 s per-tenant-day
snapshot, C10) and the small derived tables into it (C18); this phase adds the read cache, tunes the remote read and the background
work, and adds the measurement and the documentation.

## Read path

1. **Read cache** (C22): `ParquetReadCache` in the core, under the work area, bounded by `Storage:ReadCache:MaxBytes` (object
   stores 5 GiB, Local 0 = off), least recently used. The resolver hands DuckDB the cached local path of a level 1 or 2 file when
   present; on a miss it hands the remote URI and queues a background `GetFileAsync` (one per key at a time, drawn from
   `MaxBackgroundRequests`), written to a temp name and renamed when complete, so a reader never sees a partial file. Level-0
   files are never cached (they live minutes). A file whose catalog row is forgotten is evicted on the next snapshot that no
   longer lists it, and by LRU otherwise; the cache is emptied at startup (the work area belongs to one process start).
2. **Wide scopes** (README P3): a request's sliced reads reuse the days it already resolved (`DefineAsync` asks the catalog only
   for days a wider slice adds); unhinted trace detail looks `trace_index` up **newest day first** in widening chunks (1, 3, 7
   days, then the rest) and stops at the first chunk that finds the trace. `trace_index` is already sorted by `(trace_hi,
   trace_lo)`, so a file costs its footer plus about one row group. The trace list's ±1 day `trace_index` margin is kept
   (it is how anchors are confirmed) and is measured by phase 6 run 4.
3. **DuckDB caches and threads** (README P7): `parquet_metadata_cache=true` (footers, the main per-file cost; safe because a
   visible file is never overwritten, README "Correctness" 5); `enable_http_metadata_cache=true`; the external file cache, if the
   bundled DuckDB has it, with its size set explicitly inside `memory_limit` (phase 0 spike 4 records the setting); `threads`
   defaulting to 4 x cores on object stores, since remote reads wait on I/O.
4. **Request budgets** (README P8): reads and ingestion draw from `Storage:MaxConcurrentRequests`; the compactor, clean-up,
   retention and read-cache downloads from `Storage:MaxBackgroundRequests`, so a compaction burst never queues a user's read.

## Instrumentation

- The `Keryhe.Telemetry.Parquet.Storage` meter: `storage_requests{backend, op, outcome, caller}` (`caller` = `read`, `ingest`,
  `compaction`, `cleanup`, `bucket_sweep`, `retention`, `read_cache`, `observer`), `storage_request_duration{backend, op}`,
  `storage_bytes{backend, direction}`, `read_cache{result=hit|miss}`, `read_cache_bytes`, `duckdb_secret_refreshes{backend, reason}`
  (R8).
- The `Keryhe.Telemetry.Parquet.Catalog` meter: `catalog_statements{op, outcome}`, `catalog_statement_duration{op}`,
  `catalog_cache{result=hit|miss}`, `catalog_commits{outcome=ok|already_committed|lost}`, `catalog_swaps{outcome=won|lost|unknown_won|unknown_lost}`
  (R1), `staged_abandoned`, `inlined_rows{table}` (resident, unmoved), `compaction_partitions{kind}`.
- `flush_unavailable_seconds{signal}` on `IngestionMetrics` (C6; phase 1 decision 7).
- The engine's `Debug` `parquet-sql` line gains `requests=`, `cached=` and `resolve_ms=`.

## Stress harness

- `--storage local|s3|azure` (default `local`) with `--provider Parquet`: starts MinIO or Azurite next to the PostgreSQL that
  holds the control plane and the catalog, passes the storage options and a per-run `RootId` to both hosts.
- `--storage-latency-ms <n>` puts Toxiproxy between the hosts and the emulator (O20).
- `--catalog-provider postgresql|sqlserver|mysql` (default `postgresql`): which database holds the catalog (and, as today, the
  control plane), so phase 6 can compare catalog providers.
- `--collectors <n>` (default 1) and `--tenant-affinity on|off` (default `on`): with several collectors, `on` sends each tenant's
  exports to one collector (a hash of its key), `off` spreads them round-robin, so phase 6 can show what C21 is worth.
- `--linger-ms <n>` and `--inline-derived on|off` pass through to both hosts, so the cost matrix (phase 6 run 10) needs no
  configuration files.
- `ParquetObserver` builds its store and DuckDB setup from the same options. During the run it reads file, row and inlined-row
  counts from the catalog every 60 s (phase 1 decision 18), never walking the bucket (README P10); the end-of-run correctness count
  is DuckDB over the live files the catalog lists plus the inlined rows, through its own store, with requests tagged
  `caller=observer` and reported apart. It reports the storage and catalog meters (by op, per signal, per tenant-hour of ingested
  data, per caller) in a "Storage and catalog" report section, with **write requests per active tenant-hour against C23's 250**,
  routine LIST count (expected 0), the data's freshness (export to first visible in a list query, sampled by the marker probes)
  and the request cost per tenant-month at prices passed with `--price-put`, `--price-get`, `--price-list` (per 1,000; no
  defaults are built in, since prices change). That section is O21's cost model.
- Profiles tag the storage, catalog and the cost settings (`...-s3`, `...-azure`, `...-lat25`, `...-catsqlserver`, `...-l30`,
  `...-noinline`, `...-c2rr`): runs on different storage, catalog or cost settings are not comparable with each other.

## Documentation

- `src/Keryhe.Telemetry.Parquet/README.md`: the catalog (what it holds, sizing it, its own connection string and provider,
  sharing one catalog between roots with `RootId` and why a location belongs to one root, what an outage does: C6), and that it is
  **the system of record and must be backed up with the control plane** (C24); choosing a backend and which packages to reference
  (the `Kind` and catalog switches a host writes); the shared configuration and the per-backend defaults (C19, C20, C22) with the
  freshness and crash-loss trade-off of the linger and the memory per collector; **a "Cost" section**: where requests go, the
  phase 6 arithmetic per tenant-hour, the levers in order (linger, tenant affinity, inlining, a write-ahead log not yet built), and
  network charges; **tenant affinity** with load-balancer examples (C21: consistent hashing on the `Authorization` header in
  Envoy, NGINX and an AWS/Azure load balancer where it supports it, and what happens without it); offline extensions; moving a
  root between backends (O16); fresh install only (C7); what "emulators only" did not prove. Links to the three storage package
  READMEs.
- `src/Keryhe.Telemetry.Parquet.Local/README.md`: `RootPath`, the one-machine rule.
- `src/Keryhe.Telemetry.Parquet.AwsS3/README.md` and `src/Keryhe.Telemetry.Parquet.AzureBlob/README.md`: configuration;
  credentials (chains, secrets, never appsettings, how the DuckDB secret is refreshed, and how to check a role or managed
  identity, since tests do not); **deployment guidance (C25)**: one region; the S3 gateway VPC endpoint (and what a NAT gateway
  costs instead) or Azure's service/private endpoint; Standard/Hot, why not IA/Cool/Intelligent-Tiering under 90 days, and the
  tag-based lifecycle rule for `_day/` files on longer retention; versioning off or with noncurrent expiry; the incomplete
  multipart rule; encryption is the bucket's. AwsS3's says the endpoint override is for MinIO in development, and `ChecksumMode`.
  AzureBlob's recommends a flat-namespace account and says ADLS Gen2 is untested.
- The PostgreSQL, SQL Server and MySQL package READMEs: `Add<Provider>ParquetCatalogServices`, the catalog script, and any
  `InlineDerived` default phase 0 changed for that provider.
- `docs/CONFIGURATION.md`: `Telemetry:Parquet:RootId`, `Catalog:*`, `Storage:*` and its `AwsS3`/`AzureBlob`/`ReadCache` sections,
  the changed `Linger*` and `Compaction:*` defaults per backend, `ConnectionStrings:Catalog`; `WriterId` and
  `Compaction:OrphanSweepMinutes` removed. `docs/SETUP.md`: the catalog script and the object-storage option.
- `CLAUDE.md`: the projects table (the three storage packages; the catalog classes in the provider packages); the schema
  component list (`parquet-catalog`, `apply-schema.sh catalog`); the Parquet notes block (the catalog replaces manifests: layout,
  staged commits and tokens, inlined derived tables, compaction swaps, retention by key, lease, writer registry, the unavailable
  hold; storage kinds, the seam, the switches, the read cache; test filters `ParquetS3`/`ParquetAzure`; harness flags).
- `plans/parquet-duckdb/README.md`: U4's "later phase" points here, and its manifest design is marked replaced by this plan's
  catalog.

## Done when

The read cache, budgets and meters are in, the harness runs `smoke` on `s3` and `azure` end to end with the correctness ledger
balanced and the "Storage and catalog" section filled, and the docs above are updated.
