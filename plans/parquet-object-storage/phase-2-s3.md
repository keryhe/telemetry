# Phase 2: S3

Part of [Parquet on object storage](README.md).

## Steps

1. `S3ObjectStore` (`AWSSDK.S3`): one `IAmazonS3` singleton, from `S3:ServiceUrl`/`ForcePathStyle`/`Region` and either explicit keys
   or the SDK's default chain (O12). `PutFileAsync` through `TransferUtility` above `MultipartThresholdBytes`. Conditional puts per
   phase 0 spike 1. `ListAsync` pages `ListObjectsV2` (1,000 per page) with `Delimiter`. `DeletePrefixAsync` lists and deletes in
   `DeleteObjects` batches of 1,000, and reports per-key errors.
2. Error mapping for `IFlushErrorClassifier` (`ParquetFlushErrorClassifier`): `503 SlowDown`, `500`, timeouts and connection errors
   are transient; `403`/`404 NoSuchBucket` are permanent (a misconfiguration: the batch is refused rather than retried forever). The
   SDK's own retry stays on (standard mode), so the worker's retry is a second layer; its count is logged.
3. `S3DuckDbStorageSetup`: `LOAD httpfs` (and `aws` when the credential chain is used), the secret from the same options
   (`ENDPOINT` without scheme, `URL_STYLE 'path'` when `ForcePathStyle`, `USE_SSL` from the scheme), and the extension settings (O6).
   `ReadUri` = `s3://<bucket>/<prefix>/<key>`.
4. `StorageOptions` accepts `Kind=S3`; validation per the README. Registration picks the store from `Kind` on both hosts.
5. `schema/apply-schema.sh telemetry parquet` keeps taking a local root; for S3 the collector creates `_schema.json` on first start
   (as it does locally), and the script prints that.

## Tests

- `ObjectStoreContractTests` against MinIO.
- `ParquetS3ProviderContainer` in the test infrastructure: MinIO (a bucket per run) plus the control-plane PostgreSQL, as the
  local one composes it. `Provider=ParquetS3` runs every shared base the local Parquet fixture runs (and the compacted variant).
- A fault-injecting `IObjectStore` decorator (unit suite) returns `503 SlowDown` for a fraction of calls: ingestion retries and
  stores each batch once; a read surfaces an error, not a wrong answer.

## Done when

`--filter Provider=ParquetS3` passes with the same tests as `Provider=Parquet`, and the contract and fault tests pass.
