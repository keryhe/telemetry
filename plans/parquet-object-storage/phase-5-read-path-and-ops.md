# Phase 5: read path, harness and documentation

Part of [Parquet on object storage](README.md).

## Read path

1. **Partition discovery from the range** (O18): a bounded scope lists a tenant's `date=` prefixes once (delimiter) and each day's
   `hour=` and `_day/` prefixes; an unbounded one lists the tenant's dates. No recursive walks.
2. **Manifest cache** (`ParquetManifestCache`, singleton): generation manifests by name until they are deleted (the compactor's own
   deletes evict on the API host; a missing object on read evicts elsewhere); writer manifests and listings for
   `ManifestCacheSeconds`. The compactor and `ManifestWriter` read uncached: they need the latest state.
3. **Concurrency**: a scope's partitions resolve concurrently, bounded by `MaxConcurrentRequests` per host.
4. **DuckDB caches**: `enable_http_metadata_cache=true`; the external file cache if DuckDB 1.5's has one for the backend (phase 0
   records it). Footer reads per file are the main cost, so these matter.
5. **If phase 0 spike 4 asked for it**: a per-day manifest index (`date=<d>/_index.json`, the hours present and their manifest names
   with ETags), written by the compactor, so a 24 h resolve is one GET per day plus the changed manifests.

## Instrumentation

The `Keryhe.Telemetry.Parquet.Storage` meter (README "Reads"): `storage_requests{backend, op, outcome}`,
`storage_request_duration{backend, op}`, `storage_bytes{backend, direction}`, `manifest_cache{result=hit|miss}`. The engine's `Debug`
`parquet-sql` line gains `requests=` and `resolve_ms=`.

## Stress harness

- `--storage local|s3|azure` (default `local`) with `--provider Parquet`: starts MinIO or Azurite next to the control-plane
  PostgreSQL, passes the storage options to both hosts.
- `--storage-latency-ms <n>` puts Toxiproxy between the hosts and the emulator (O20).
- `ParquetObserver` builds its store and DuckDB setup from the same options; it reports the storage meter (requests by op, per
  signal, per tenant-hour of ingested data) in a "Storage requests" report section, which is O21's cost model.
- Profiles tag the storage (`...-s3`, `...-azure`, `...-lat25`): runs on different storage are not comparable with each other.

## Documentation

- `src/Keryhe.Telemetry.Parquet/README.md`: choosing a backend; configuration; credentials (chains, secrets, never appsettings,
  and how to check a role or managed identity, since tests do not); bucket settings (versioning off or with noncurrent expiry,
  lifecycle rule for incomplete multipart uploads and for `_writers/` debris, encryption is the bucket's); request-cost arithmetic
  from phase 6; offline extensions; moving a root between backends (O16); writer ids on Kubernetes; what "emulators only" did not
  prove.
- `docs/CONFIGURATION.md`: `Telemetry:Parquet:Storage`. `CLAUDE.md`: the Parquet notes block (storage kinds, the seam, test filters
  `ParquetS3`/`ParquetAzure`, harness flags). `docs/SETUP.md`: the object-storage option.
- `plans/parquet-duckdb/README.md`: U4's "later phase" points here.

## Done when

The cache and meter are in, the harness runs `smoke` on `s3` and `azure` end to end with the correctness ledger balanced, and the
docs above are updated.
