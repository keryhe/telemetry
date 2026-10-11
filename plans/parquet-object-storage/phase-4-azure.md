# Phase 4: Azure Blob

Part of [Parquet on object storage](README.md). Everything here goes in a new project, **`Keryhe.Telemetry.Parquet.AzureBlob`**
(O22): packable like the other providers (version 2.0.0), referencing `Keryhe.Telemetry.Parquet`, `Azure.Storage.Blobs` and
`Azure.Identity`, nothing else.

## Steps

1. `AzureBlobObjectStore` (`Azure.Storage.Blobs`): a `BlobContainerClient` from `AzureBlob:ServiceUri` + `Container` and
   `DefaultAzureCredential`, or from `AzureBlob:ConnectionString` (Azurite, SAS, account key). `PutFileAsync` with
   `StorageTransferOptions` (block upload above `AzureBlob:MultipartThresholdBytes`). `ListAsync` with
   `GetBlobsByHierarchyAsync(delimiter)`. `GetFileAsync` with `DownloadToAsync` (the read cache, C22).
   `DeleteManyAsync` through `BlobBatchClient` (256 per batch; one by one on Azurite if phase 0 spike 5 found no batch there). No conditional requests.
2. `AzureBlobStorageErrorClassifier : IStorageErrorClassifier`: `503 ServerBusy`, `500`, timeouts and connection errors **unavailable** (C6); `403`,
   `404 ContainerNotFound` permanent.
3. `AzureBlobDuckDbStorageSetup`: `LOAD azure`, a secret from the connection string, or `PROVIDER credential_chain` with
   `ACCOUNT_NAME` taken from `ServiceUri` (R8). A credential-chain secret is refreshed as the README's "DuckDB" section says (the
   extension's own refresh if it has one, otherwise recreated from the SDK's token before expiry and after an authentication
   error). `ReadUri` = `az://<container>/<prefix>/<key>`. If phase 0 spike 1 found the extension cannot reach Azurite, this phase
   adds the minimal read-through download instead (local copy in the work area, deleted after the statement) and says so in the
   README.
4. `AzureBlobStorageOptions` (`Telemetry:Parquet:Storage:AzureBlob`) with its `Validate()`: a missing container, or neither a
   `ServiceUri` nor a `ConnectionString`, fails startup. `AddParquetAzureBlobStorage(configuration)` registers the store, setup,
   classifier, options and the `IParquetStorageBackend` marker (`Kind = "AzureBlob"`); the core's `Kind` validator accepts
   `AzureBlob`. Both hosts' switches gain the `AzureBlob` case and reference the project; so do `TestInfrastructure`,
   `IntegrationTests` and `StressTests`. `Telemetry.sln` gains the project.
5. The package's own `README.md` (filled in by phase 5), with C25's guidance (one region, service or private endpoint and its
   per-GB charge, Hot tier, versioning, a **flat-namespace** account). An ADLS Gen2 account (hierarchical namespace) is not
   special-cased and **not tested** (Azurite has no hierarchical namespace, O2): the README says so, and that blob batch delete
   and empty-directory behaviour there are unverified.

## Tests

`ObjectStoreContractTests` against Azurite (a subclass in `IntegrationTests` under `Provider=ParquetAzure`, R14);
`ParquetAzureProviderContainer` (Azurite plus the PostgreSQL holding the control plane and catalog); `Provider=ParquetAzure`
runs the same shared bases. The core, Local and AwsS3 projects do not reference `Azure.*` (the same check as phase 3's).

## Done when

`--filter Provider=ParquetAzure` passes with the same tests as `Provider=Parquet` plus the Azurite contract tests.
