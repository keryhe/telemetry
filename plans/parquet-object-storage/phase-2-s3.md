# Phase 2: AWS S3

Part of [Parquet on object storage](README.md). Everything here goes in a new project, **`Keryhe.Telemetry.Parquet.AwsS3`**
(O22): packable like the other providers (version 2.0.0), referencing `Keryhe.Telemetry.Parquet` and `AWSSDK.S3`, nothing else.
The target is AWS; MinIO, through the endpoint override, is how it is developed and tested.

## Steps

1. `AwsS3ObjectStore` (`AWSSDK.S3`): one `IAmazonS3` singleton, from `AwsS3:ServiceUrl`/`ForcePathStyle`/`Region` and either explicit
   keys or the SDK's default chain (O12). `PutFileAsync` through `TransferUtility` above `AwsS3:MultipartThresholdBytes`. Conditional
   puts per phase 0 spike 1. `ListAsync` pages `ListObjectsV2` (1,000 per page) with `Delimiter`. `DeletePrefixAsync` lists and
   deletes in `DeleteObjects` batches of 1,000, and reports per-key errors.
2. `AwsS3StorageErrorClassifier : IStorageErrorClassifier`, consulted by `ParquetFlushErrorClassifier`: `503 SlowDown`, `500`,
   timeouts and connection errors are transient; `403`/`404 NoSuchBucket` are permanent (a misconfiguration: the batch is refused
   rather than retried forever). The SDK's own retry stays on (standard mode), so the worker's retry is a second layer; its count is
   logged.
3. `AwsS3DuckDbStorageSetup`: `LOAD httpfs` (and `aws` when the credential chain is used), the secret from the same options
   (`ENDPOINT` without scheme, `URL_STYLE 'path'` when `ForcePathStyle`, `USE_SSL` from the scheme), and the extension settings (O6).
   `ReadUri` = `s3://<bucket>/<prefix>/<key>`.
4. `AwsS3StorageOptions` (`Telemetry:Parquet:Storage:AwsS3`) with its `Validate()` (README "Configuration").
   `AddParquetAwsS3Storage(configuration)` registers the store, setup, classifier, options and the `IParquetStorageBackend` marker
   (`Kind = "AwsS3"`); the core's `Kind` validator accepts `AwsS3`. Both hosts' switches gain the `AwsS3` case and reference the
   project; so do `TestInfrastructure`, `Keryhe.Telemetry.Parquet.Tests` and `StressTests`. `Telemetry.sln` gains the project.
5. `schema/apply-schema.sh telemetry parquet` keeps taking a local root; for S3 the collector creates `_schema.json` on first start
   (as it does locally), and the script prints that.
6. The package's own `README.md` (filled in by phase 5): AWS configuration, credentials, bucket settings; MinIO for development.

## Tests

- `ObjectStoreContractTests` against MinIO.
- `ParquetS3ProviderContainer` in the test infrastructure: MinIO (a bucket per run) plus the control-plane PostgreSQL, as the
  local one composes it. `Provider=ParquetS3` runs every shared base the local Parquet fixture runs (and the compacted variant).
- A fault-injecting `IObjectStore` decorator (unit suite) returns `503 SlowDown` for a fraction of calls: ingestion retries and
  stores each batch once; a read surfaces an error, not a wrong answer.
- `Keryhe.Telemetry.Parquet` and `Keryhe.Telemetry.Parquet.Local` do not reference `AWSSDK.*` (a test reads the project
  assets, or a build check greps them).

## Done when

`--filter Provider=ParquetS3` passes with the same tests as `Provider=Parquet`, and the contract and fault tests pass.
