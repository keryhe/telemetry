# Phase 3: AWS S3

Part of [Parquet on object storage](README.md). Everything here goes in a new project, **`Keryhe.Telemetry.Parquet.AwsS3`**
(O22): packable like the other providers (version 2.0.0), referencing `Keryhe.Telemetry.Parquet` and `AWSSDK.S3`, nothing else.
The target is AWS; MinIO, through the endpoint override, is how it is developed and tested.

## Steps

1. `AwsS3ObjectStore` (`AWSSDK.S3`): one `IAmazonS3` singleton, from `AwsS3:ServiceUrl`/`ForcePathStyle`/`Region`/`ChecksumMode`
   (README P14: `WhenRequired` by default when `ServiceUrl` is set, as phase 0 spike 1 decided) and either explicit keys or the
   SDK's default chain (O12). `PutFileAsync` through `TransferUtility` above `AwsS3:MultipartThresholdBytes`.
   `ListAsync` pages `ListObjectsV2` (1,000 per page) with `Delimiter`. `GetFileAsync` (the read cache, C22) is a
   `GetObject` to a work-area file. `DeleteManyAsync` deletes in `DeleteObjects` batches of 1,000 and reports per-key errors (a
   missing key is success). No conditional requests (README "The primitive layer").
2. `AwsS3StorageErrorClassifier : IStorageErrorClassifier`, consulted by `ParquetFlushErrorClassifier`: `503 SlowDown`, `500`,
   timeouts and connection errors are **unavailable** (C6: the flush holds rather than dropping after `MaxFlushRetries`); `403`/`404 NoSuchBucket` are permanent (a misconfiguration: the batch is refused
   rather than retried forever). The SDK's own retry stays on (standard mode), so the worker's retry is a second layer; its count
   is logged. A PUT whose response was lost and is retried rewrites the same staged key, which is harmless (README
   "Correctness" 1, 5).
3. `AwsS3DuckDbStorageSetup`: `LOAD httpfs` (and `aws` when the credential chain is used), the secret from the same options
   (`REGION` always, `ENDPOINT` without scheme, `URL_STYLE 'path'` when `ForcePathStyle`, `USE_SSL` from the scheme), and the
   extension settings (O6). With the credential chain, the secret refreshes (README R8): `REFRESH auto` if phase 0 spike 3 found
   it, otherwise the setup recreates the secret from the SDK's resolved credentials before their expiry and after an
   authentication error. `ReadUri` = `s3://<bucket>/<prefix>/<key>`.
4. `AwsS3StorageOptions` (`Telemetry:Parquet:Storage:AwsS3`) with its `Validate()`: a missing bucket, a `ServiceUrl` without a
   scheme, or `AccessKeyId` without `SecretAccessKey` (or the reverse) fail startup. `AddParquetAwsS3Storage(configuration)`
   registers the store, setup, classifier, options and the `IParquetStorageBackend` marker (`Kind = "AwsS3"`); the core's `Kind`
   validator accepts `AwsS3`. Both hosts' switches gain the `AwsS3` case and reference the project; so do `TestInfrastructure`,
   `IntegrationTests` and `StressTests`. `Telemetry.sln` gains the project.
5. `schema/apply-schema.sh telemetry parquet` keeps taking a local root; for S3 the bucket must exist (the provider does not
   create buckets) and the collector registers the root in the catalog on first start; the script prints that.
6. The package's own `README.md` (filled in by phase 5): AWS configuration, credentials, bucket settings, the deployment guidance
   of C25 (one region, the S3 gateway VPC endpoint, Standard storage, versioning, the multipart rule); MinIO for development.

## Tests

- `ObjectStoreContractTests` against MinIO: a subclass in `IntegrationTests` under `Provider=ParquetS3` (it needs Docker, R14).
- `ParquetS3ProviderContainer` in the test infrastructure: MinIO (a bucket per run) plus the PostgreSQL that holds the control
  plane and the catalog, as the local one composes it. `Provider=ParquetS3` runs every shared base the local Parquet fixture runs
  (and the compacted variant).
- A fault-injecting `IObjectStore` decorator (unit suite, `Keryhe.Telemetry.Parquet.Tests`) with two modes: **fail** (returns
  `503 SlowDown` instead of performing a fraction of calls) and **applied, then failed** (performs the call, then throws as if the
  response were lost). Under both: ingestion stores each batch once; a **sustained** fail mode (every call fails for longer than the whole
  `MaxFlushRetries` schedule) holds without dropping and resumes (C6); a compaction either swaps or leaves its inputs live, never
  both or neither; retention finishes a half-deleted day on the next sweep; a read surfaces an error, not a wrong answer.
- A credential-refresh test with MinIO's STS short-lived credentials if spike 3 found it workable; otherwise a unit test of the
  setup's refresh schedule with a controllable `TimeProvider`.
- `Keryhe.Telemetry.Parquet` and `Keryhe.Telemetry.Parquet.Local` do not reference `AWSSDK.*` (a test reads the project assets,
  or a build check greps them).

## Done when

`--filter Provider=ParquetS3` passes with the same tests as `Provider=Parquet` plus the MinIO contract tests, and the fault and
refresh tests pass.
