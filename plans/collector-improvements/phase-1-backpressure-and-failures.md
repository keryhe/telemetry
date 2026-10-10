# Phase 1: backpressure and flush failures

Written 2026-10-08. Part of [collector improvements](README.md). Three changes to the ingestion path:

1. **Reject quickly when full.** Today `RecordCountGate.AcquireAsync` waits until there is room or the client gives
   up. The exporter then times out (SDK default 10 s) holding a connection and a deserialized request, and retries the
   same data. Instead, wait a short, bounded time, then reject with `UNAVAILABLE` and a `RetryInfo` delay, as the OTel
   Collector's `memory_limiter` does.
2. **Split failing batches.** `TelemetryIngestionWorker.FlushWithRetryAsync` retries any exception up to
   `MaxFlushRetries` times, then drops the whole merged batch, which can hold many tenants' exports. A permanent error
   (one over-length value, say) costs about 6 s of retries and drops everything merged with it. Instead, classify the
   error; on a permanent one, split the batch in half repeatedly so only the bad records are dropped.
3. **Accepted/refused counters and health endpoints.** Count what each signal accepted and refused, with the reason,
   and expose liveness and readiness so a load balancer can route around a saturated or disconnected instance.

**Two ingestion workers (reviewed 2026-10-09 against `ec5b098`).** Since this plan was written the ClickHouse redesign
replaced the shared `TelemetryIngestionWorker` on that provider with `ClickHouseIngestionWorker`
(`Keryhe.Telemetry.ClickHouse/Services`; `AddClickHouseCollectorServices` removes the shared registration). It drains
the same channel and releases the same gates, but buffers records per UTC day, flushes on a linger
(`Telemetry:ClickHouse:Ingestion:LingerMilliseconds`, 2,500; late days 30,000) or a batch size, mints an
`insert_deduplication_token` per sealed piece that is reused on every retry, drops records older than the retention
window at ingest, and has its own `FlushWithRetryAsync` (sharing `MaxFlushRetries` and the backoff settings). Change 1
is in the services and repositories and applies to both workers unchanged; changes 2 and 3 have to be made in both
workers (steps 9 and 10).

## Decisions

| # | Decision |
|---|---|
| 1 | The gate waits at most `Telemetry:Ingestion:MaxGateWaitMilliseconds` (default 2,000), then the export is rejected. `0` rejects at once when full. A negative value keeps today's unbounded wait (an escape hatch, not documented as recommended). **ClickHouse:** its worker releases the gate one whole day buffer at a time, when the buffer flushes, and at the defaults that is on the linger, never on size (`MaxSpanBatchRecords` and the other batch sizes are 300,000, above the 200,000 `MaxQueued*` gate, so a buffer cannot fill before the gate does). Under saturation room appears in large steps about every linger plus flush time, so a 2,000 ms wait would refuse exports the next flush would admit. When `MaxGateWaitMilliseconds` is not set, the ClickHouse registration defaults it to `LingerMilliseconds` + 2,000 (4,500), and startup warns when it is set below the linger. Confirm in the stress run (ClickHouse's ramp trips on gate wait p95 > 250 ms near its ceiling, `plans/clickhouse-redesign/results.md`) |
| 2 | A rejection is gRPC `UNAVAILABLE` with a `google.rpc.RetryInfo` detail of `Telemetry:Ingestion:RejectRetryDelayMilliseconds` (default 1,000) plus up to 50% random jitter. `UNAVAILABLE`, not `RESOURCE_EXHAUSTED`: the OTLP spec makes `RESOURCE_EXHAUSTED` retryable only when `RetryInfo` is present, so an exporter that ignores status details would drop the data; every exporter retries `UNAVAILABLE` |
| 3 | Before converting a request, a service refuses it at once if the gate is at capacity **and has been refusing callers for at least the whole bounded wait** (`RecordCountGate.SaturatedFor`). A gate that has only just filled does not trigger it, because the next flush may free room within the wait (as built; the first draft refused on any full gate, which made the wait pointless). The bounded wait still applies after conversion, when the exact count is known |
| 4 | A rejected export was never enqueued, so nothing is acknowledged that is not stored. Partial-success responses stay for permanent per-request problems only (invalid data), as today |
| 5 | Flush errors are classified per provider as **transient** (retry with the current backoff) or **permanent** (no retry; split). An exception the classifier does not recognise is transient, which is today's behaviour |
| 6 | On a permanent error the batch is split in half and each half flushed (with the transient-retry policy) until a failing piece is a single record, which is dropped. The number of extra flush attempts per original batch is capped by `Telemetry:Ingestion:MaxSplitFlushes` (default 64); what remains when the cap is reached is dropped |
| 7 | `records_dropped` is always tagged with a `reason`: `retries_exhausted`, `permanent`, `split_cap`, `shutdown`, and ClickHouse's `out_of_retention`. Partly done: `IngestionMetrics.RecordDropped` already takes an optional `reason`, and `ClickHouseIngestionWorker` already sets `retries_exhausted`, `out_of_retention` and `shutdown`; the shared worker still records it untagged. This phase makes the parameter required, tags the shared worker's two call sites, and adds `permanent` and `split_cap`. Existing dashboards that sum it keep working |
| 8 | New counters on `IngestionMetrics`: `records_accepted` (`signal`, `tenant`) and `records_refused` (`signal`, `tenant`, `reason`: `throttled`, `shutting_down`, `invalid`, `unauthenticated` is already `auth_failures`). Tenant cardinality is the number of tenants, which is acceptable for this meter |
| 9 | Health: gRPC `grpc.health.v1` on the OTLP endpoints (Kubernetes gRPC probes, gRPC-aware load balancers) **and** HTTP `/healthz/live` and `/healthz/ready` on a separate management endpoint (Kestrel's OTLP endpoints are HTTP/2-only, and HTTP probes use HTTP/1.1) |
| 10 | Ready means: not shutting down, the control-plane key lookup succeeded within the last `ReadinessControlPlaneSeconds` (default 60) or has not been needed (on ClickHouse `RetentionWindowCache`'s refresh also reads the control plane; it does not affect readiness, since a failed refresh keeps the last windows), and no signal's gate has been at capacity for longer than `ReadinessSaturatedSeconds` (default 10). Live means the process is running and the ingestion worker's loops have not faulted |

## Steps

### Reject quickly when full

1. **`RecordCountGate`.** Add `TryAcquireAsync(int count, TimeSpan maxWait, CancellationToken)` returning `bool`
   (a linked timeout over the existing poll loop; keep the "admit unconditionally when empty" rule). Keep
   `AcquireAsync` for callers that want the unbounded wait. Add `IsSaturated` (resident at or over capacity) for the
   pre-check (decision 3) and the readiness check.
2. **`IngestionRejectedException`** in Core (`Reason`, `RetryAfter`). The three write repositories in
   `Core/Data/Write` call `TryAcquireAsync` with `TelemetryIngestionOptions.MaxGateWaitMilliseconds` and throw it on
   `false`. A pre-check helper on `TelemetryIngestionChannel` throws it when the gate is saturated.
3. **Services.** `TraceService`, `LogService`, `MetricService`: call the pre-check before `Convert*`; catch
   `IngestionRejectedException` ahead of the generic `OperationCanceledException`/`Exception` catches and throw an
   `RpcException` with status `UNAVAILABLE` and a `RetryInfo` detail. Build it with `Grpc.StatusProto`
   (`Google.Rpc.Status.ToRpcException()`) and `Google.Api.CommonProtos` (`RetryInfo`), or write the
   `grpc-status-details-bin` trailer directly if those packages are unwanted. Record `records_refused`.
4. **Options.** `MaxGateWaitMilliseconds`, `RejectRetryDelayMilliseconds` on `TelemetryIngestionOptions`, validated
   at startup; add them to `Collector.Server/appsettings.json`. `MaxGateWaitMilliseconds` is nullable (unset = the
   provider's default, decision 1); `AddClickHouseCollectorServices` supplies the ClickHouse default through a
   `PostConfigure` and the linger warning.
5. **Clients we own.**
   - Stress harness `Load/OtlpExporter`: a new `ExportOutcome.Throttled` for `UNAVAILABLE` with `RetryInfo`;
     ledger it as not landed (the server never enqueued it); retry after the given delay, up to the scenario's
     deadline. The report shows throttled exports per window.
   - `Scenarios/RampEvaluator`: `gate_wait_p95` is now capped by `MaxGateWaitMilliseconds`, so it stops being a
     useful trip signal on its own. Add a `throttled_rate` criterion; keep `gate_wait_p95` reported.
   - `TestDataGenerator/Sinks/OtlpSink` already retries `UNAVAILABLE`/`RESOURCE_EXHAUSTED`; make it honour the
     `RetryInfo` delay when present.
6. **Check real exporters.** Confirm against the OTLP spec and each exporter's source which honour `RetryInfo` (the
   OTel Collector's `otlp` exporter, the .NET, Java and Go SDK exporters). Record the result in the Collector
   README. Every one of them must at least retry `UNAVAILABLE`.

### Split failing batches

7. **Classifier.** `IFlushErrorClassifier` in Core (`FlushErrorKind Classify(Exception)`), registered by each
   provider's `Add<Provider>CollectorServices`; a default that returns `Transient` is registered with `TryAdd` by
   `AddKeryheTelemetryCollector`. Starting lists, to verify against each driver:
   - PostgreSQL (`PostgresException.SqlState`): permanent for class `22` (data exception) and `23` (integrity),
     transient for `08`, `40001`, `40P01`, `53xxx`, `57P0x`, and for `NpgsqlException` with no SQL state.
   - SQL Server (`SqlException.Number`): permanent for 2628 and 8152 (truncation), 515 (null), 547 (constraint),
     245/8114 (conversion), and `SqlBulkCopy`'s "invalid column length" `InvalidOperationException`; transient for
     1205, -2, 4060, 40197, 40501, 40613, 49918-49920.
   - MySQL (`MySqlException.ErrorCode`): permanent for 1406 (data too long), 1366 (incorrect value), 1048 (null),
     3140 (invalid JSON); transient for 1205, 1213, 2002, 2006, 2013.
   - ClickHouse (`ClickHouseServerException.ErrorCode`): permanent for type and parse errors (`CANNOT_PARSE_*`,
     `TYPE_MISMATCH`, `ILLEGAL_TYPE_OF_ARGUMENT`); transient for `MEMORY_LIMIT_EXCEEDED` (241), `TOO_MANY_PARTS`
     (252), timeouts and network errors. Since the redesign every ClickHouse attribute, name and body column is an
     unbounded `String`/`Map(LowCardinality(String), String)`, ids are converted by `ClickHouseIds` before the insert,
     and the writer builds typed rows, so a permanent error from client data should be rare there (most would be a
     writer bug). The classifier is still worth having, mostly so a bug drops one record instead of a 300,000-record
     buffer.
8. **Atomicity audit.** Before splitting, check what each bulk writer has committed when a flush throws part-way.
   PostgreSQL commits the reference upserts before the data transaction (harmless to repeat). Every relational
   writer's metrics path writes several tables per flush; a retried half may store rows a second time in a table that
   had already succeeded. Spans and logs tolerate that (plain appends, decision 7 of schema 3.0.0); for data points,
   either make the flush write the data-point tables only once all of them can succeed, or record the duplication as
   accepted, measured in the per-provider test below. Write the outcome into this plan before step 9.

   ClickHouse, as the redesign left it: a raw insert of `ParallelFlushMinRows` (50,000) rows or more is cut into up to
   four concurrent pieces under tokens `{token}:{table}:{i}`, and a metrics flush runs its five points-table inserts
   together, so a failed flush can have landed partly, even within one table. A **retry** of the same piece is safe
   (same tokens, ClickHouse discards the repeat). A **split** is not: the halves are different row sets and must get
   new tokens, because ClickHouse deduplicates on the token alone and would silently discard a different block sent
   under a token it has already seen. Whatever pieces of the failed flush had landed are therefore stored a second time
   when its halves are flushed. Accept that (reads already tolerate duplicate spans, logs and points from client
   re-sends) and measure it in the ClickHouse test. Derived rows (`trace_index`, rollups, catalog, series) are written
   after the raw insert and never fail the flush (`derived_rows_dropped`), so they follow whichever pieces land.
9. **Worker.** In `TelemetryIngestionWorker`, `FlushWithRetryAsync` returns an outcome (`Ok`, `Transient`
   exhausted, `Permanent`). On `Permanent`, a new `FlushSplittingAsync` bisects as in decision 6, flushing each piece
   through `FlushWithRetryAsync`. `onFlushed` (rollups) runs per successful piece; the gate is still released once,
   for the whole batch, in the existing `finally`; commit lag is recorded once when the batch is finished. A
   single-record drop logs the signal, tenant, service, the record's id (trace/span id, or time and severity for a
   log, metric name for a metric) and the exception, at `Warning`, rate-limited like the auth warnings.

   The same in `ClickHouseIngestionWorker`: its `FlushWithRetryAsync` returns the outcome, and `FlushBufferAsync`
   bisects a piece that failed permanently, minting a new token per half (step 8). It already releases the gate once
   per buffer and records commit lag only when every piece succeeded; keep that. A buffer can hold 300,000 records, so
   isolating one bad record takes about 2 x 19 flushes, inside the 64 cap; several bad records in one buffer will reach
   the cap. Each extra flush is one small insert, so one part per table per flush; acceptable for a rare error, but log
   the split count. Put the bisection in one Core helper both workers call (it needs only the flush delegate, the
   classifier and the cap), not two copies.
10. **Metrics.** `RecordDropped(signal, count, reason)` with `reason` required (decision 7); update
    `ScenarioAnalyzer`/`RunResult` in the stress harness to break drops down by reason (today it sums
    `records_dropped` into one "records dropped" series beside the ClickHouse-only `derived_rows_dropped` and
    `late_buffer_records`).

### Counters and health

11. **Counters.** `records_accepted` after a successful enqueue in each service; `records_refused` in each refusal
    path, including invalid input caught by the existing `ArgumentException` handler.
12. **gRPC health.** Add `Grpc.AspNetCore.HealthChecks`, map the health service in `MapKeryheTelemetryCollector()`
    (it must not require the collector authorization policy), and publish readiness through it.
13. **HTTP health.** `AddHealthChecks()` with a `CollectorReadinessCheck` (decision 10; the control-plane state comes
    from a singleton `ControlPlaneHealth` that `CachingTenantResolver`, which is scoped, updates with the time of its
    last successful lookup and of its last failure). The
    host adds a named Kestrel endpoint `Management` (`Http1`) and maps `/healthz/live` and `/healthz/ready` with
    `RequireHost` on that endpoint's port only. `PlaintextTransportGuard` must allow a plaintext `Management` endpoint
    bound to a loopback or private address, or one listed in a new `Telemetry:Collector:ManagementEndpoints` setting;
    a public plaintext management endpoint still fails startup outside Development.

## Tests

- **Unit (no Docker):** `RecordCountGate.TryAcquireAsync` (admits within the wait, refuses after it, still admits an
  oversized batch into an empty gate, cancellation); the worker's split with a fake `ITelemetryBulkWriter` that
  throws a permanent error for batches containing marked records (only marked records dropped, rollup hook sees every
  other record once, gate fully released, split cap honoured, transient errors still retried, not split).
- **`CollectorAuth` suite (`TestServer`):** a full gate gives `UNAVAILABLE` with a parsable `RetryInfo`; the
  pre-check rejects before conversion (assert with a request whose conversion would throw); health endpoints answer
  without a key; readiness flips when the gate is held at capacity and when the fake `IApiKeyLookup` throws.
- **Per provider (Testcontainers):** a real poison record flushed with 1,999 good records: 1,999 stored, one dropped
  with `reason=permanent`, no retries recorded. On PostgreSQL, SQL Server and MySQL a span name longer than the
  `VARCHAR(255)`/`NVARCHAR(255)` column does it (PostgreSQL's `spans.name` is `VARCHAR(255)` too, SQLSTATE 22001).
  ClickHouse has no length limit and no known client-data poison record, so its split is tested through
  `ClickHouseIngestionWorkerTests`' fake `IClickHouseTokenWriter` (only marked records dropped, a new token per half,
  the retry of a piece reusing its token), plus one Testcontainers run counting the duplicates a split of a
  partly-landed parallel flush stores (step 8).
- **Stress:** `ramp` and `ramp-write-only` on PostgreSQL, SQL Server and ClickHouse (its own worker and linger,
  decision 1), compared with a run from the current commit. Expect gate wait capped near `MaxGateWaitMilliseconds`,
  throttled exports instead of `DeadlineExceeded` failures, the correctness ledger still balancing, and no drop of
  reason `retries_exhausted` below the ceiling. On ClickHouse also check the throttled rate does not rise below the
  ceiling found in `plans/clickhouse-redesign/results.md` (about 83,000 records/s write-only), which would mean the
  ClickHouse default wait is too short.

## Documentation

- `docs/CONFIGURATION.md`: the new `Telemetry:Ingestion` keys, `Telemetry:Collector:ManagementEndpoints`, the
  readiness thresholds.
- Collector README: what a client sees when the collector is full, which exporters honour `RetryInfo`, the health
  endpoints and the management port.
- `CLAUDE.md`: the "Write path decoupling" paragraph (bounded wait, rejection, split), the new instruments, the
  `RampEvaluator` criterion.

## Done when

- A saturated collector answers `UNAVAILABLE` + `RetryInfo` within `MaxGateWaitMilliseconds` and never holds an
  export until the client's deadline.
- A batch containing one permanently bad record stores every other record.
- Accepted, refused (by reason) and dropped (by reason) are visible in `dotnet-counters`, and the stress report shows
  them.
- A load balancer can probe readiness over gRPC health or HTTP.

## Implementation notes (2026-10-09)

Built as planned, with these differences and results:

- **Decision 3** as amended above. `ThrowIfSaturated` is on `TelemetryIngestionChannel`; the proof that the pre-check runs before the repository is a gate-wait measurement test (`BackpressureTests.The_precheck_refuses_before_the_request_reaches_the_repository`).
- **No `Grpc.StatusProto`/`Google.Api.CommonProtos`.** The two `google.rpc` messages needed (`Status`, `RetryInfo`) are compiled from small wire-compatible protos in `Collector/Protos/google/rpc`; the services attach `grpc-status-details-bin` themselves (`ExportRejections`). `Grpc.AspNetCore.HealthChecks` was added for the gRPC health service.
- **Step 8 (atomicity audit) outcome.** Relational writers roll their data transaction back on failure, so a split repeats nothing. ClickHouse can land part of a flush (parallel pieces, the five points tables); a retry reuses its tokens and is safe, a split mints new tokens per half and so stores again whatever had landed. Accepted; not measured against a real ClickHouse because no client-data record is known to fail there (unbounded strings), so the split is tested with the fake token writer only.
- **Classifiers verified against the real drivers** (`PoisonRecord*Tests`): a 300-character span name is a permanent error on PostgreSQL (22001), SQL Server (bulk-copy truncation) and MySQL (1406), each recognised by its provider classifier; 1,999 of 2,000 are stored, the bad one dropped, in about 2 x log2(n) flushes and with no retry. A control batch with 255-character names is stored in one flush.
- **Step 6 (exporters).** The OTLP specification (checked 2026-10-09) says `UNAVAILABLE` is retryable, the client MUST throttle itself on it, and it should wait `RetryInfo.retry_delay`. The OpenTelemetry Collector's `otlp` exporter was run against this collector (phase 5 check, `deploy/otel-collector/README.md`) and **does** honour `RetryInfo` (`Throttle (1.46s)` in its log, then a backed-off retry, no loss). Which SDK exporters read `RetryInfo`, as opposed to retrying `UNAVAILABLE` on their own backoff, was **not** verified against their source; the Collector README says so. The stress harness and the test data generator honour it.
- **Health.** The management endpoint defaults to `http://127.0.0.1:5119` (`Kestrel:Endpoints:Management`); the stress harness picks a free port for it.
- **Test isolation.** Test classes that count the process-wide `Keryhe.Telemetry.Ingestion` meter share one xUnit collection (`IngestionMeter`) so they do not see one another's measurements.
