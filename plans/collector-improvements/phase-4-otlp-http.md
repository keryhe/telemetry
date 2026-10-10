# Phase 4: OTLP over HTTP

Written 2026-10-08. Part of [collector improvements](README.md). After phases 1 to 3 (it maps phase 1's and
phase 3's rejections and reuses phase 2's converter and limits).

The collector accepts OTLP over gRPC only. OTLP/HTTP is the other transport in the spec, and some clients default to
it (the Java agent 2.x uses `http/protobuf`; some serverless and proxy setups allow only HTTP/1.1). Add
`POST /v1/traces`, `/v1/logs` and `/v1/metrics`, accepting binary protobuf and JSON, optionally gzip-compressed, with
the same key authentication, gate, limits and conversion as gRPC.

## Decisions

| # | Decision |
|---|---|
| 1 | Paths are the spec's: `/v1/traces`, `/v1/logs`, `/v1/metrics`, optionally under `Telemetry:Collector:HttpBasePath` (default empty) for hosts that mount them elsewhere. `POST` only |
| 2 | Request `Content-Type` `application/x-protobuf` or `application/json`; anything else is `415`. The response uses the request's content type |
| 3 | `Content-Encoding: gzip` is accepted; `identity` or none as well; anything else is `415`. Decompression is bounded by `MaxReceiveMessageSizeBytes` (phase 2) on the **decompressed** size, read in a streaming loop, so a small compressed body cannot expand without limit |
| 4 | Status mapping, per the spec: success `200` with the `Export*ServiceResponse` (partial success included); malformed body `400` with a `google.rpc.Status` body; missing, malformed, invalid or expired key `401` (with a `google.rpc.Status` body naming the reason, as the gRPC path names it); phase 1's rejection and phase 3's tenant share and rate rejections `503` with `Retry-After` (seconds, rounded up) and a `Status` body; phase 3's failed-attempt throttling `429` without `Retry-After`; control-plane lookup failure `503` with `Retry-After`; shutting down `503`; body too large `413` |
| 5 | Authentication stays in `ApiKeyAuthenticationHandler`. `CollectorEndpointMetadata` gains a `Protocol` (`Grpc`/`Http`); the handler's challenge writes the gRPC trailers-only response for `Grpc` and the HTTP status and `Status` body for `Http` |
| 6 | The three gRPC services stop owning conversion and enqueue. Each signal gets an ingestor (`OtlpTraceIngestor`, etc.) taking the protobuf request and tenant id and returning an `IngestResult` (accepted counts, rejected counts with message, or throwing phase 1's `IngestionRejectedException`). The gRPC service and the HTTP endpoint are thin adapters mapping that result to their protocol |
| 7 | JSON follows the OTLP JSON mapping, which differs from protobuf's standard JSON: `traceId`, `spanId`, `parentSpanId` (and log/exemplar/link ids) are **hex** strings, not base64; enum values are integers; 64-bit integers may be strings or numbers; unknown fields are ignored |
| 8 | Ports. Production: the existing TLS endpoint (`Https`, 7057) changes from `Http2` to `Http1AndHttp2`, so ALPN serves gRPC and HTTP/1.1 clients on one port. Development: h2c cannot share a port with HTTP/1.1, so `appsettings.Development.json` adds a plaintext `OtlpHttp` endpoint on `http://localhost:5118` (`Http1AndHttp2`) beside the h2c `Http` endpoint on 5117 |
| 9 | The endpoints are minimal-API endpoints mapped by `MapKeryheTelemetryCollector()` with the same authorization policy and `CollectorEndpointMetadata` as the gRPC services. Not MVC: the collector host has no controllers and should not gain the MVC stack for three routes |
| 10 | No CORS for these endpoints (see the README's out-of-scope note on browser clients) |

## Steps

1. **Ingestors.** Extract conversion and enqueue from `TraceService`, `LogService`, `MetricService` into
   `OtlpTraceIngestor`, `OtlpLogIngestor`, `OtlpMetricIngestor` (Collector project, singletons or scoped to match
   the write repositories), returning `IngestResult`. The gRPC services become adapters. No behaviour change: the
   existing `CollectorAuth` tests and a stress `smoke` run must pass unchanged before going further.
2. **Body reading.** An `OtlpHttpBodyReader`: checks `Content-Type` and `Content-Encoding`, streams the body
   (through `GZipStream` when compressed) into a pooled buffer with the decompressed-size limit, and parses with the
   generated `Parser` for protobuf or the JSON reader (step 3) for JSON. Disable Kestrel's own
   `MaxRequestBodySize` on these endpoints only if it is lower than the configured limit; otherwise leave it as the
   outer guard.
3. **JSON.** `OtlpJsonReader`: `Google.Protobuf.JsonParser` with `IgnoreUnknownFields`, after rewriting the hex id
   fields to base64 in a pre-pass over the JSON (a `Utf8JsonReader`/`Utf8JsonWriter` copy that knows the id field
   names at each nesting level). Responses written with `JsonFormatter` and the reverse rewrite (the response types
   carry no ids today, so this may be a plain format). If the pre-pass turns out awkward, a hand-written reader for
   the three request types is the fallback; decide in this step and note it here.
4. **Endpoints.** `MapPost` for the three paths in `MapKeryheTelemetryCollector()`, each: read the tenant claim
   (`TenantClaims`), read the body, call the ingestor, map the result (decision 4). Write the `google.rpc.Status`
   body in the request's content type.
5. **Authentication.** Add `Protocol` to `CollectorEndpointMetadata`; branch the handler's challenge and forbid.
   `auth_failures` gains no tag (the signal tag already distinguishes; add `protocol` only if wanted).
6. **Kestrel and transport.** Update `Collector.Server/appsettings.json` and `appsettings.Development.json`
   (decision 8). Check `PlaintextTransportGuard` treats the new development endpoint like the existing h2c one
   (Development only) and still fails a plaintext `OtlpHttp` endpoint outside Development. Check that switching
   `Https` to `Http1AndHttp2` does not change gRPC behaviour (ALPN must pick `h2` for gRPC clients).
7. **Counters.** `records_accepted`/`records_refused` from phase 1 gain a `protocol` tag (`grpc`, `http`).
8. **Clients we own.**
   - `TestDataGenerator`'s live `SdkSink`: a `Generator:Protocol` option (`grpc` default, `http/protobuf`) passed
     to the SDK's OTLP exporter, so live data can be sent over HTTP.
   - Stress harness: optional, an HTTP mode for `Load/OtlpExporter`. Not required for this phase; note it in the
     harness README as a follow-up if not done.

## Tests

- **`CollectorAuth` suite (`TestServer`, no Docker):** for each signal, protobuf and JSON, plain and gzip: accepted
  records read back from the ingestion channel equal those from the same request sent over gRPC. Plus: `415` for a
  wrong content type and for `br` encoding; `413` for a decompressed body over the limit (with a small compressed
  body); `401` with reason for each key failure; `503` + `Retry-After` when the gate is full; `400` for a corrupt
  body; unknown JSON fields ignored.
- **JSON conformance:** the spec's example payloads (traces, logs, metrics) from the opentelemetry-proto repository,
  vendored as test files with their licence notice, parsed and compared with the protobuf equivalents.
- **Interop:** the generator's `SdkSink` over `http/protobuf` into a local collector, data visible in the UI. One
  manual check with the OTel Collector's `otlphttp` exporter (phase 5's local setup can be reused if phase 5 runs
  first).

## Documentation

- Collector README: the endpoints, content types, compression, status codes, ports, and the exporter settings for
  `http/protobuf` (`OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, endpoint without the `/v1/...` suffix for the
  generic variable, with it for the per-signal ones).
- `docs/CONFIGURATION.md`: `HttpBasePath`, the endpoint changes.
- `CLAUDE.md`: default ports, the ingestor split, "Collector authentication" (both protocols), the development port.

## Done when

- An SDK configured for `http/protobuf` or `http/json` exports to the collector with the same results, limits,
  backpressure and authentication as gRPC.
- The gRPC services contain no conversion or enqueue logic of their own.

## Implementation notes (2026-10-09)

Built as planned, with these differences and results:

- **Ingestors first, unchanged behaviour.** The conversion and enqueue code moved from the three services into `OtlpTraceIngestor`, `OtlpLogIngestor` and `OtlpMetricIngestor` (scoped, `IngestAsync(request, tenantId, protocol, ct)`); the gRPC services are now about twenty lines each. The existing `CollectorAuth` tests and a PostgreSQL smoke passed on the refactor before any HTTP code was added. Two small corrections rode along: a conversion failure now reports the whole export as rejected (it used to report 0 with an error message), and a metrics failure to enqueue now counts the data points it lost. A shutting-down collector is now a retryable `UNAVAILABLE` with `RetryInfo` (it was a bare `UNAVAILABLE`).
- **JSON.** A DOM pass (`JsonNode`) rewrites `traceId`, `spanId` and `parentSpanId` from hex to base64 and Google.Protobuf's `JsonParser` does the rest (enum integers, string or number 64-bit integers, unknown fields ignored). The fallback of a hand-written reader was not needed. The spec-shaped JSON in the tests is written by hand following the OTLP specification's examples; the opentelemetry-proto example files were not vendored.
- **Kestrel.** `Https` is `Http1AndHttp2`; a real-Kestrel test (`OtlpHttpKestrelTests`, a self-signed certificate) shows one TLS port serving a gRPC client on HTTP/2 and an OTLP/HTTP client on HTTP/1.1. Development gets a plaintext HTTP/1.1-only `OtlpHttp` endpoint on 5118 (the plan said `Http1AndHttp2`, which on plaintext means HTTP/1.1 only anyway). **A proxy that terminates TLS and forwards plaintext needs the gRPC endpoint `Http2`-only and a separate `Http1` endpoint for OTLP/HTTP**, since plaintext cannot negotiate; this is in `docs/CONFIGURATION.md` and the Collector README.
- **A saturated queue refuses an HTTP export before its body is read**, using the same pre-check as gRPC.
- **Interop with a client that is not ours:** the real OpenTelemetry .NET SDK, exporting over `http/protobuf` through the generator's new `Generator:Protocol` option, into the real endpoints, queues exactly the number of spans, log records and metrics the same SDK sends over gRPC (`HttpProtobufInteropTests`). It found that the SDK's logs exporter fails at shutdown with `ObjectDisposedException` when it takes its `HttpClient` from the logging provider's container, so the generator gives its HTTP exporters an `HttpClient` of their own. Not done: a manual check with the OpenTelemetry Collector's `otlphttp` exporter (phase 5 runs that setup), and an HTTP mode for the stress harness's load generator (follow-up; the harness's exporter is gRPC-only).
- **The generator's SDK providers listen by activity-source name process-wide**, so two tests that simulate the same tenant at once export each other's spans; the interop test uses a tenant of its own.
