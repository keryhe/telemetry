# Phase 2: input limits

Written 2026-10-08. Part of [collector improvements](README.md). After phase 1 (it reports into phase 1's refusal and
drop counters). Two protections at the edge, for clients that send directly with no OTel Collector of their own:

1. **Limit by bytes, not just record count.** The gates bound resident records (`MaxQueued*`, 200,000 each), but a
   record's memory ranges from a few hundred bytes to megabytes depending on its attributes and body. The OTel
   Collector's queue can be sized in bytes and its `memory_limiter` watches real memory. Add a byte budget alongside
   the count, and make the maximum gRPC message size explicit.
2. **Attribute and field limits.** Cap attribute counts and lengths, events and links per span, and log bodies, and
   clip every value stored in a sized column to that column's size. Today an over-length span name, service name or
   severity text is likely a permanent flush failure on SQL Server and MySQL (`NVARCHAR(255)`/`VARCHAR(255)`); after
   phase 1 that drops the record, after this phase it is stored, clipped.

## Decisions

| # | Decision |
|---|---|
| 1 | Each signal's gate also bounds resident **bytes**, measured as the protobuf size of the request part that produced the records (`CalculateSize()` on each `ResourceSpans`/`ResourceLogs`/`ResourceMetrics`). In-memory models are larger than their protobuf, so this is a proxy; the default budget allows for it |
| 2 | `Telemetry:Ingestion:MaxQueuedBytesPerSignal`, default 256 MiB. A request is admitted when both its records and its bytes fit, or the gate is empty (the existing one-oversized-batch allowance). `0` turns the byte budget off |
| 3 | `Telemetry:Collector:MaxReceiveMessageSizeBytes`, default 4 MiB (gRPC's and the OTel Collector's default, now explicit and configurable). It applies to the decompressed message; a larger one is refused with `RESOURCE_EXHAUSTED` by grpc-dotnet. Phase 4 applies the same limit to OTLP/HTTP bodies |
| 4 | Limits are applied in conversion, never by rejecting: values are truncated and lists cut, and the record's own `dropped_attributes_count` / `dropped_events_count` / `dropped_links_count` is increased by what was cut, as an SDK would. Truncation is counted on a `records_truncated` counter (`signal`, `limit`) |
| 5 | Defaults (`Telemetry:Ingestion:Limits`): 128 attributes per record, resource or scope; attribute key 256 characters; attribute string value 16,384 characters; 128 events and 128 links per span; log body 65,536 characters; nested array/map values at most 32 levels deep and 1,024 elements per level. `0` means unlimited for any of them |
| 6 | Values stored in sized columns are clipped to the column size on every provider, from one table of sizes in Core, not per provider: span name, service name, severity text, event name, scope name and version, metric name and unit, schema URL. The sizes are the smallest across the four schemas |
| 7 | Truncation never splits a UTF-16 surrogate pair and is by characters, which is what `NVARCHAR(n)` and MySQL `VARCHAR(n)` count. ClickHouse `String` and PostgreSQL `text` have no limit, so decision 6 only matters for the other two, but is applied everywhere so a record looks the same on every provider |
| 8 | Resource attributes are truncated **before** the resource is hashed, so the same oversized resource always maps to the same row |

## Steps

### Byte budget

1. **Gate.** Give `RecordCountGate` a second budget (bytes) and make `Acquire`/`TryAcquireAsync`/`Release` take both.
   Keep the name or rename to `IngestionGate`; the `resident_records` gauge gets a `resident_bytes` sibling.
2. **Carrying the size to the release.** The worker releases by `sizeOf(batch)` today. Carry each export's bytes in
   the existing `ConditionalWeakTable` on `TelemetryIngestionChannel` (beside the `MarkEnqueued` stamp), read back
   in the drain loop with `TryTakeEnqueued`, summed per merged batch and released with it. A missing entry releases
   0 bytes and logs once (that would be a bug, not data).
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
   `schema/*-Telemetry.sql`. Audit all four scripts while building it (the SQL Server script has `NVARCHAR(255)`
   names, `NVARCHAR(256)` event name, `NVARCHAR(63)` unit, `NVARCHAR(2048)` schema URL; MySQL similar). Clip in
   conversion. Add a test that parses the four scripts and fails when a sized column is missing from the table or
   its size differs, so a future schema change cannot silently reintroduce a permanent failure.
9. **Metrics.** `records_truncated` on `IngestionMetrics`.

## Tests

- **Unit:** the converter at each limit and limit + 1 (count, key, value, depth, elements), surrogate pairs at the
  cut, dropped counts increased, `0` meaning unlimited; resource hash stable for a resource that needed truncation.
- **Unit:** the gate with both budgets (admits when both fit, waits on either, empty-gate allowance, release of both).
- **`CollectorAuth` suite:** a message over `MaxReceiveMessageSizeBytes` refused, plain and gzip-compressed; a span
  with a 1,000-character name and 500 attributes accepted, then read from the ingestion channel clipped to 255 and 128.
- **Per provider:** the phase 1 poison-record test now stores the record, clipped, with no drop.
- **Stress:** one `smoke` run per provider to confirm conversion cost has not moved export latency noticeably.

## Documentation

- `docs/CONFIGURATION.md`: `MaxQueuedBytesPerSignal`, `MaxReceiveMessageSizeBytes`, every `Limits` key.
- Collector README: what is truncated and how a user can tell (dropped counts in the UI, `records_truncated`).
- `CLAUDE.md`: "Write path decoupling" (two budgets), the converter, the `ColumnLimits` test.

## Done when

- Resident memory is bounded by bytes as well as records.
- No value a client can send fails a flush because of its length.
- Every truncation is visible in a counter and in the record's dropped counts.
