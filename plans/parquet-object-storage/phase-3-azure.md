# Phase 3: Azure Blob

Part of [Parquet on object storage](README.md).

## Steps

1. `AzureBlobObjectStore` (`Azure.Storage.Blobs`): a `BlobContainerClient` from `Azure:ServiceUri` + `Container` and
   `DefaultAzureCredential`, or from `Azure:ConnectionString` (Azurite, SAS, account key). `PutFileAsync` with
   `StorageTransferOptions` (block upload above `MultipartThresholdBytes`). Conditional puts with `BlobRequestConditions`
   (`IfNoneMatch = ETag.All`, `IfMatch`); a 409 `BlobAlreadyExists` and a 412 both map to "lost". `ListAsync` with
   `GetBlobsByHierarchyAsync(delimiter)`. `DeletePrefixAsync` through `BlobBatchClient` (256 per batch).
2. Error mapping: `503 ServerBusy`, `500`, timeouts transient; `403`, `404 ContainerNotFound` permanent.
3. `AzureDuckDbStorageSetup`: `LOAD azure`, a secret from the connection string or `PROVIDER credential_chain`;
   `ReadUri` = `az://<container>/<prefix>/<key>`. If phase 0 spike 3 found the extension cannot reach Azurite, this phase adds the
   minimal read-through download instead (local copy in the work area, deleted after the statement) and says so in the README.
4. `StorageOptions` accepts `Kind=AzureBlob`.

## Tests

`ObjectStoreContractTests` against Azurite; `ParquetAzureProviderContainer`; `Provider=ParquetAzure` runs the same shared bases.

## Done when

`--filter Provider=ParquetAzure` passes with the same tests as `Provider=Parquet`.
