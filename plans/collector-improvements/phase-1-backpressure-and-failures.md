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

## Decisions

| # | Decision |
|---|---|
| 1 | The gate waits at most `Telemetry:Ingestion:MaxGateWaitMilliseconds` (default 2,000), then the export is rejected. `0` rejects at once when full. A negative value keeps today's unbounded wait (an escape hatch, not documented as recommended) |
| 2 | A rejection is gRPC `UNAVAILABLE` with a `google.rpc.RetryInfo` detail of `Telemetry:Ingestion:RejectRetryDelayMilliseconds` (default 1,000) plus up to 50% random jitter. `UNAVAILABLE`, not `RESOURCE_EXHAUSTED`: the OTLP spec makes `RESOURCE_EXHAUSTED` retryable only when `RetryInfo` is present, so an exporter that ignores status details would drop the data; every exporter retries `UNAVAILABLE` |
| 3 | Before converting a request, a service checks whether the gate is already at capacity and rejects at once if so. The bounded wait (decision 1) still applies after conversion, when the exact count is known |
| 4 | A rejected export was never enqueued, so nothing is acknowledged that is not stored. Partial-success responses stay for permanent per-request problems only (invalid data), as today |
| 5 | Flush errors are classified per provider as **transient** (retry with the current backoff) or **permanent** (no retry; split). An exception the classifier does not recognise is transient, which is today's behaviour |
| 6 | On a permanent error the batch is split in half and each half flushed (with the transient-retry policy) until a failing piece is a single record, which is dropped. The number of extra flush attempts per original batch is capped by `Telemetry:Ingestion:MaxSplitFlushes` (default 64); what remains when the cap is reached is dropped |
| 7 | `records_dropped` gains a `reason` tag: `retries_exhausted`, `permanent`, `split_cap`, `shutdown`. Existing dashboards that sum it keep working |
| 8 | New counters on `IngestionMetrics`: `records_accepted` (`signal`, `tenant`) and `records_refused` (`signal`, `tenant`, `reason`: `throttled`, `shutting_down`, `invalid`, `unauthenticated` is already `auth_failures`). Tenant cardinality is the number of tenants, which is acceptable for this meter |
| 9 | Health: gRPC `grpc.health.v1` on the OTLP endpoints (Kubernetes gRPC probes, gRPC-aware load balancers) **and** HTTP `/healthz/live` and `/healthz/ready` on a separate management endpoint (Kestrel's OTLP endpoints are HTTP/2-only, and HTTP probes use HTTP/1.1) |
| 10 | Ready means: not shutting down, the control-plane key lookup succeeded within the last `ReadinessControlPlaneSeconds` (default 60) or has not been needed, and no signal's gate has been at capacity for longer than `ReadinessSaturatedSeconds` (default 10). Live means the process is running and the ingestion worker's loops have not faulted |

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
   at startup; add them to `Collector.Server/appsettings.json`.
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
     (252), timeouts and network errors.
8. **Atomicity audit.** Before splitting, check what each bulk writer has committed when a flush throws part-way.
   PostgreSQL commits the reference upserts before the data transaction (harmless to repeat). The ClickHouse writer
   and every writer's metrics path write several tables per flush; a retried half may store rows a second time in a
   table that had already succeeded. Spans and logs tolerate that (plain appends, decision 7 of schema 3.0.0); for
   data points, either make the flush write the data-point tables only once all of them can succeed, or record the
   duplication as accepted, measured in the step 12 test. Write the outcome into this plan before step 9.
9. **Worker.** In `TelemetryIngestionWorker`, `FlushWithRetryAsync` returns an outcome (`Ok`, `Transient`
   exhausted, `Permanent`). On `Permanent`, a new `FlushSplittingAsync` bisects as in decision 6, flushing each piece
   through `FlushWithRetryAsync`. `onFlushed` (rollups) runs per successful piece; the gate is still released once,
   for the whole batch, in the existing `finally`; commit lag is recorded once when the batch is finished. A
   single-record drop logs the signal, tenant, service, the record's id (trace/span id, or time and severity for a
   log, metric name for a metric) and the exception, at `Warning`, rate-limited like the auth warnings.
10. **Metrics.** `RecordDropped(signal, count, reason)`; update `ScenarioAnalyzer`/`RunResult` in the stress harness
    to break drops down by reason.

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
- **Per provider (Testcontainers):** a real poison record (e.g. a span name longer than the 255-character column on
  SQL Server and MySQL; find the equivalent on PostgreSQL and ClickHouse or note that none exists) flushed with 1,999
  good records: 1,999 stored, one dropped with `reason=permanent`, no retries recorded.
- **Stress:** `ramp` and `ramp-write-only` on PostgreSQL and SQL Server, compared with a run from the current commit.
  Expect gate wait capped near `MaxGateWaitMilliseconds`, throttled exports instead of `DeadlineExceeded` failures,
  the correctness ledger still balancing, and no drop of reason `retries_exhausted` below the ceiling.

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
