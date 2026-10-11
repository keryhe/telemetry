# Phase 3: Azure Blob

Part of [Parquet on object storage](README.md). Everything here goes in a new project, **`Keryhe.Telemetry.Parquet.AzureBlob`**
(O22): packable like the other providers (version 2.0.0), referencing `Keryhe.Telemetry.Parquet`, `Azure.Storage.Blobs` and
`Azure.Identity`, nothing else.

## Steps

1. `AzureBlobObjectStore` (`Azure.Storage.Blobs`): a `BlobContainerClient` from `AzureBlob:ServiceUri` + `Container` and
   `DefaultAzureCredential`, or from `AzureBlob:ConnectionString` (Azurite, SAS, account key). `PutFileAsync` with
   `StorageTransferOptions` (block upload above `AzureBlob:MultipartThresholdBytes`). Conditional puts with `BlobRequestConditions`
   (`IfNoneMatch = ETag.All`, `IfMatch`); a 409 `BlobAlreadyExists` and a 412 both map to "lost". `ListAsync` with
   `GetBlobsByHierarchyAsync(delimiter)`. `DeletePrefixAsync` through `BlobBatchClient` (256 per batch).
2. `AzureBlobStorageErrorClassifier : IStorageErrorClassifier`: `503 ServerBusy`, `500`, timeouts transient; `403`,
   `404 ContainerNotFound` permanent.
3. `AzureBlobDuckDbStorageSetup`: `LOAD azure`, a secret from the connection string or `PROVIDER credential_chain`;
   `ReadUri` = `az://<container>/<prefix>/<key>`. If phase 0 spike 3 found the extension cannot reach Azurite, this phase adds the
   minimal read-through download instead (local copy in the work area, deleted after the statement) and says so in the README.
4. `AzureBlobStorageOptions` (`Telemetry:Parquet:Storage:AzureBlob`) with its `Validate()`. `AddParquetAzureBlobStorage(configuration)`
   registers the store, setup, classifier, options and the `IParquetStorageBackend` marker (`Kind = "AzureBlob"`); the core's
   `Kind` validator accepts `AzureBlob`. Both hosts' switches gain the `AzureBlob` case and reference the project; so do
   `TestInfrastructure`, `Keryhe.Telemetry.Parquet.Tests` and `StressTests`. `Telemetry.sln` gains the project.
5. The package's own `README.md` (filled in by phase 5).

## Tests

`ObjectStoreContractTests` against Azurite; `ParquetAzureProviderContainer`; `Provider=ParquetAzure` runs the same shared bases.
The core, Local and AwsS3 projects do not reference `Azure.*` (the same check as phase 2's).

## Done when

`--filter Provider=ParquetAzure` passes with the same tests as `Provider=Parquet`.
