# Phase 0: spikes

Part of [Parquet on object storage](README.md). Throwaway code in a scratch project (not in the solution); the findings go to
`phase-0-results.md`, and any that change a decision are applied to the README and the later phases, marked "phase 0".

## Spikes

| # | Question | How | Changes the design if |
|---|---|---|---|
| 1 | **Do MinIO and Azurite honour conditional writes** through the .NET SDKs (`AWSSDK.S3` `PutObjectRequest.IfNoneMatch`/`IfMatch`; `Azure.Storage.Blobs` `BlobRequestConditions`)? | Two tasks race `PutIfAbsent` on one key 1,000 times: exactly one wins each round. `PutIfMatch` with a stale ETag fails. Note the status codes (412/409) each returns | An emulator ignores a header: its fixture cannot test contention, and phase 6's contention runs use the other emulator only (say so) |
| 2 | **Read-after-write and list consistency on the emulators** for the manifest read order | Rerun the first plan's spike 3 (writers commit and a compactor publishes generations while readers resolve) against MinIO and Azurite through a hand-written `IObjectStore` | A reader ever loses, duplicates or misses a file |
| 3 | **DuckDB 1.5.x (DuckDB.NET.Data.Full 1.5.5) reads the emulators**: `httpfs` with `CREATE SECRET (TYPE s3, ENDPOINT, URL_STYLE 'path', USE_SSL false)` against MinIO; `azure` with a `CONNECTION_STRING` secret against Azurite. `read_parquet([list])` over 100 files, row-group pruning visible in the bytes read. Offline: `SET autoinstall_known_extensions=false`, `extension_directory` with pre-downloaded extensions, on macOS arm64 and Linux x64 | The azure extension cannot reach Azurite: the Azure fixture reads through a local download (a minimal read-through cache moves into phase 3) |
| 4 | **The resolve cost**: requests and wall time of today's `ParquetFileResolver` for a 1 h and a 24 h scope (spans; 2 writers; with and without a day generation) against MinIO behind Toxiproxy at 0, 10 and 25 ms | Count the store calls; time them serial and with 16 concurrent | The 24 h resolve at 25 ms is above about 300 ms even concurrent: phase 5 gets a per-day manifest index (one object listing a day's hours' manifests) instead of only a cache |
| 5 | **One credential configuration for both sides** | Explicit keys feed both the SDK client and the DuckDB secret; confirm DuckDB's `credential_chain` reads the same `AWS_*` environment variables the SDK does, and Azure's the same `AZURE_*` ones | They differ: the engine creates its secret from what the SDK resolved (temporary credentials refreshed before expiry), not from DuckDB's own chain |
| 6 | **Testcontainers modules**: `Testcontainers.Minio` and `Testcontainers.Azurite` 4.15.0, start time, and Toxiproxy (`Testcontainers.Toxiproxy`) between the host and MinIO | Start each from the test infrastructure project | A module is missing or broken: use a generic container definition |

## Done when

`phase-0-results.md` answers the six questions with numbers, and the README and phases 1 to 6 carry the changes.
