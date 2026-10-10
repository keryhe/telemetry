# Phase 2: input limits

Written 2026-10-08. Part of [collector improvements](README.md). After phase 1 (it reports into phase 1's refusal and
drop counters). Two protections at the edge, for clients that send directly with no OTel Collector of their own:

1. **Limit by bytes, not just record count.** The gates bound resident records (`MaxQueued*`, 200,000 each), but a
   record's memory ranges from a few hundred bytes to megabytes depending on its attributes and body. The OTel
   Collector's queue can be sized in bytes and its `memory_limiter` watches real memory. Add a byte budget alongside
   the count, and make the maximum gRPC message size explicit.
2. **Attribute and field limits.** Cap attribute counts and lengths, events and links per span, and log bodies, and
   clip every value stored in a sized column to that column's size. Today an over-length span name or service name is
   a permanent flush failure on PostgreSQL, SQL Server and MySQL (all three declare `VARCHAR(255)`/`NVARCHAR(255)`
   for them; MySQL also for severity text); after phase 1 that drops the record, after this phase it is stored,
   clipped. ClickHouse (schema 4.0.0) has no sized string columns, but copies the resource and scope attributes onto
   every span, log record and point (`Map(LowCardinality(String), String)`), so unbounded attributes cost it storage
   on every row rather than once per resource; the attribute limits matter there for that reason.

## Decisions

| # | Decision |
|---|---|
| 1 | Each signal's gate also bounds resident **bytes**, measured as the protobuf size of the request part that produced the records (`CalculateSize()` on each `ResourceSpans`/`ResourceLogs`/`ResourceMetrics`). In-memory models are larger than their protobuf, so this is a proxy; the default budget allows for it |
| 2 | `Telemetry:Ingestion:MaxQueuedBytesPerSignal`, default 256 MiB. A request is admitted when both its records and its bytes fit, or the gate is empty (the existing one-oversized-batch allowance). `0` turns the byte budget off |
| 3 | `Telemetry:Collector:MaxReceiveMessageSizeBytes`, default 4 MiB (gRPC's and the OTel Collector's default, now explicit and configurable). It applies to the decompressed message; a larger one is refused with `RESOURCE_EXHAUSTED` by grpc-dotnet. Phase 4 applies the same limit to OTLP/HTTP bodies |
| 4 | Limits are applied in conversion, never by rejecting: values are truncated and lists cut, and the record's own `dropped_attributes_count` / `dropped_events_count` / `dropped_links_count` is increased by what was cut, as an SDK would. Truncation is counted on a `records_truncated` counter (`signal`, `limit`) |
| 5 | Defaults (`Telemetry:Ingestion:Limits`): 128 attributes per record, resource or scope; attribute key 256 characters; attribute string value 16,384 characters; 128 events and 128 links per span; log body 65,536 characters; nested array/map values at most 32 levels deep and 1,024 elements per level. `0` means unlimited for any of them |
| 6 | Values stored in sized columns are clipped to the column size on every provider, from one table of sizes in Core, not per provider: span name, service name, severity text, event name, scope name and version, metric name and unit, schema URL. The sizes are the smallest across the three relational schemas (ClickHouse's are unbounded). Service name also sizes the relational rollup tables' `service_name` (`VARCHAR(255) NOT NULL DEFAULT ''`): clipping in conversion keeps an over-length name from becoming a permanent `RollupWorker` append failure too, which today would be retried every flush interval until `MaxBufferedRows` drops it |
| 7 | Truncation never splits a UTF-16 surrogate pair and is by characters, which is what PostgreSQL `VARCHAR(n)`, `NVARCHAR(n)` and MySQL `VARCHAR(n)` all count. ClickHouse `String` and PostgreSQL `TEXT` have no limit, but the clip is applied everywhere so a record looks the same on every provider |
| 8 | Resource attributes are truncated **before** the resource is hashed, so the same oversized resource always maps to the same row on the relational providers (ClickHouse hashes no resource; its metric id hashes tenant, service, name and type, so it needs the service and metric name clipped before that hash for the same reason) |

## Steps

### Byte budget

1. **Gate.** Give `RecordCountGate` a second budget (bytes) and make `Acquire`/`TryAcquireAsync`/`Release` take both.
   Keep the name or rename to `IngestionGate`; the `resident_records` gauge gets a `resident_bytes` sibling.
2. **Carrying the size to the release.** The workers release by record count today. Carry each export's bytes in
   the existing `ConditionalWeakTable` on `TelemetryIngestionChannel` (beside the `MarkEnqueued` stamp), read back
   in the drain loop with `TryTakeEnqueued`. A missing entry releases 0 bytes and logs once (that would be a bug, not
   data).
   - `TelemetryIngestionWorker`: summed per merged batch and released with it.
   - `ClickHouseIngestionWorker`: its `Drain` spreads one export's records over several day buffers and drops the
     out-of-retention ones, so an export's bytes cannot be released as a unit. Apportion them per record (export bytes
     / record count, the remainder on the first record), add each record's share to a `Bytes` total on its
     `DayBuffer` (beside `Items` and `Stamps`), release a buffer's bytes with its `gate.Release(total)`, and release
     a dropped record's share at the drop. `ReportUnpersisted` needs no byte figure (nothing is released at shutdown).
3. **Services.** Compute the size per resource block during conversion (it is already walked once) and pass it to
   the write repositories. Phase 1's saturation pre-check also checks bytes.
4. **Message size.** In `AddKeryheTelemetryCollector`, `AddGrpc(o => o.MaxReceiveMessageSize = ...)` from
   `TelemetryCollectorOptions`. Confirm with a test that grpc-dotnet applies the limit after gzip decompression; if it
   does not, compressed requests need their own guard.

### Attribute and field limits

5. **One converter.** `TraceService`, `LogService` and `MetricService` each have their own `ConvertAttributes`. Move
   attribute and `AnyValue` conversion into one `OtlpAttributeConverter` (Collector project) that takes the limits and
   returns the dictionary plus the number of attributes dropped. This also saves phase 4 from a fourth copy.
6. **Limits options.** `IngestionLimitsOptions` bound from `Telemetry:Ingestion:Limits`, validated at startup.
7. **Apply.** Attribute count, key length and value length in the converter; events and links per span in
   `TraceService`; log body length in `LogService`; nesting depth and element count in `AnyValue` conversion.
   Increase the record's dropped counts as in decision 4.
8. **Column sizes.** A `ColumnLimits` table in Core listing each sized column and its smallest size across
   `schema/*-Telemetry.sql`. Audit the three relational scripts while building it (as of schema 4.0.0 all three have
   255-character service, span, scope and metric names and scope version, 256-character event name, 63-character
   unit and 2,048-character schema URLs; severity text is `VARCHAR(255)` on MySQL only, `TEXT` on PostgreSQL and
   `NVARCHAR(MAX)` on SQL Server; the rollup tables' `service_name` is 255 everywhere). Clip in conversion. Add a test
   that parses the three scripts and fails when a sized column is missing from the table or its size differs, so a
   future schema change cannot silently reintroduce a permanent failure; for ClickHouse it asserts the script still
   declares no `FixedString` or other sized string column.
9. **Metrics.** `records_truncated` on `IngestionMetrics`.

## Tests

- **Unit:** the converter at each limit and limit + 1 (count, key, value, depth, elements), surrogate pairs at the
  cut, dropped counts increased, `0` meaning unlimited; resource hash stable for a resource that needed truncation.
- **Unit:** the gate with both budgets (admits when both fit, waits on either, empty-gate allowance, release of both).
- **`CollectorAuth` suite:** a message over `MaxReceiveMessageSizeBytes` refused, plain and gzip-compressed; a span
  with a 1,000-character name and 500 attributes accepted, then read from the ingestion channel clipped to 255 and 128.
- **Per provider:** the phase 1 poison-record test (PostgreSQL, SQL Server, MySQL) now stores the record, clipped,
  with no drop. ClickHouse: a span with a 1,000-character name and 500 resource attributes is stored with the clipped
  name and 128 attributes on its row.
- **Stress:** one `smoke` run per provider to confirm conversion cost has not moved export latency noticeably.

## Documentation

- `docs/CONFIGURATION.md`: `MaxQueuedBytesPerSignal`, `MaxReceiveMessageSizeBytes`, every `Limits` key.
- Collector README: what is truncated and how a user can tell (dropped counts in the UI, `records_truncated`).
- `CLAUDE.md`: "Write path decoupling" (two budgets), the converter, the `ColumnLimits` test.

## Done when

- Resident memory is bounded by bytes as well as records.
- No value a client can send fails a flush because of its length.
- Every truncation is visible in a counter and in the record's dropped counts.

## Implementation notes (2026-10-09)

Built as planned, with these differences and results:

- **Bytes are the whole request's `CalculateSize()`**, computed once per export by the services and passed to the write repositories as `requestBytes` (an optional parameter on the three `Store*BatchAsync` methods); the per-resource-block sum is the same number. `RecordCountGate` now serialises admission under a small lock (two counters cannot be compare-and-swapped together) and keeps the "admit when empty" allowance for either budget.
- **The receive limit does not cover decompression in grpc-dotnet.** A negative test (a 1 MB gzip message against a 100 KB limit) was accepted, so the collector replaces grpc-dotnet's gzip with `BoundedGzipCompressionProvider`, which fails the call with `RESOURCE_EXHAUSTED` once decompressed bytes pass the limit. Tests: plain and gzip, each with a control that is accepted.
- **ClickHouse byte release** apportions an export's bytes equally over its records (remainder on the first); buffers and out-of-retention drops release their shares (`ClickHouseIngestionWorkerTests`).
- **Column audit result** (schema 4.0.0): the sized client-text columns are the ones in `ColumnLimits`. PostgreSQL has the same 255-character names as SQL Server and MySQL; `severity_text` is sized on MySQL only. The rollup tables' `service_name` is 255 everywhere; clipping the resource's `service.name` in conversion covers it. `ColumnLimitsTests` was negative-controlled by changing one constant.
- **Nesting.** An attribute's own array is depth 1; the 33rd level is dropped (its parent keeps whatever else it holds).
- **`records_truncated`** counts each cut value or list (tags `signal`, `limit`), not records.
- **Not done:** clipping `description` of a metric (the column is unbounded on every provider) and a hard cap on the total size of a record's attributes after the per-attribute limits (128 x 16 KB can still be 2 MB; the receive limit and the byte budget bound it).
