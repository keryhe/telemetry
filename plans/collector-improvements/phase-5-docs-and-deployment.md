# Phase 5: load balancing and client-side Collector setup

Written 2026-10-08. Part of [collector improvements](README.md). Mostly documentation. Runs last so the sample
configs can quote phase 1's throttling and phase 2's limits; it does not depend on phase 4 and can move earlier.

1. **Load balancing.** An OTLP exporter keeps one HTTP/2 connection open for as long as it runs, so a layer-4 load
   balancer pins each client to one collector instance for good, and a new instance gets no traffic until clients
   restart. The OTel Collector offers `max_connection_age` to make clients reconnect. Document what works, and add an
   optional maximum connection age.
2. **Client-side OTel Collector.** Document and test the setup the README's deployment decision relies on: a client
   runs the OTel Collector, its apps send to it with no key, and its exporter adds their Keryhe key.

## Decisions

| # | Decision |
|---|---|
| 1 | Supported topologies, documented: a gRPC-aware (layer 7) load balancer or proxy (Envoy, nginx `grpc_pass`, cloud application load balancers with HTTP/2 backends), or client-side round-robin over DNS (a Kubernetes headless service; the OTel Collector's `otlp` exporter with `balancer_name: round_robin`). A layer-4 balancer is documented as "works, but does not rebalance" |
| 2 | Optional `Telemetry:Collector:MaxConnectionAgeSeconds` (default 0 = off). When set, a connection older than the age is asked to close after its current request (`IConnectionLifetimeNotificationFeature.RequestClose()`, which sends an HTTP/2 `GOAWAY`), with up to 10% jitter so a fleet's clients do not reconnect at the same moment. In-flight requests finish |
| 3 | The sample client config is a tested file in the repository (`deploy/otel-collector/client-gateway.yaml`), not only a README snippet, so it can be run as-is |
| 4 | The sample uses the `otelcol-contrib` image only if it needs a contrib component (`file_storage` is contrib); otherwise the core image |

## Steps

### Load balancing

1. **Connection age.** A small middleware in the Collector project, added by a `UseKeryheTelemetryCollector()` call
   or inside `MapKeryheTelemetryCollector()`'s endpoint pipeline: on each request, read the connection's first-seen
   time from `IConnectionItemsFeature` (setting it on the first request), and call `RequestClose()` when the age is
   exceeded. Verify against grpc-dotnet and the Go exporter (via the OTel Collector) that the client reconnects
   without failing the in-flight export; if `RequestClose()` does not produce a graceful `GOAWAY` for HTTP/2 in
   Kestrel, record that here and drop the option, keeping the documentation.
2. **Documentation.** A "Running more than one collector" section in the Collector README: why connections stick,
   the supported topologies (decision 1) with a minimal Envoy or nginx snippet and a Kubernetes headless-service
   example, the connection-age option, and the readiness endpoints from phase 1 as the balancer's health check.

### Client-side OTel Collector

3. **Sample config** `deploy/otel-collector/client-gateway.yaml`:
   - `otlp` receiver on gRPC 4317 and HTTP 4318 (the client's own apps);
   - processors `memory_limiter`, then `batch` with `send_batch_size`/`send_batch_max_size` chosen to stay under the
     collector's `MaxReceiveMessageSizeBytes` (phase 2) with a margin, and a `timeout` of a few seconds;
   - `otlp` exporter to the Keryhe collector: `endpoint`, `headers: { authorization: "Bearer ${env:KERYHE_API_KEY}" }`,
     `compression: gzip`, `retry_on_failure` (enabled; `max_elapsed_time` raised from the 300 s default if the client
     prefers holding data to dropping it), `sending_queue` with `storage: file_storage` and a `queue_size`, and TLS
     settings (`ca_file` for a private CA);
   - `file_storage` extension with a directory;
   - one pipeline per signal.
4. **Local end-to-end check.** A `deploy/otel-collector/docker-compose.yaml` (or a script) that runs the sample
   config in a container against a locally running collector (development h2c endpoint, or the TLS one with the dev
   certificate), sends data with `telemetrygen` (`traces`, `logs`, `metrics`), and confirms it is stored for the
   key's tenant. Then:
   - stop the Keryhe collector, send more, start it again: the file queue delivers what was sent while it was down;
   - fill the gate (phase 1; lower `MaxQueuedSpans` for the test): the Collector backs off on `UNAVAILABLE` and
     honours `RetryInfo`, and nothing is lost;
   - an invalid key: the Collector logs a permanent `Unauthenticated` error and drops (document this, it is the
     client's signal to fix the key).
5. **Multi-tenant client gateway.** A short section with the routing variant (one exporter per tenant key, routed by
   a resource attribute with the `routing` connector), and the warning that the attribute must come from something the
   client's gateway trusts, not from any app that can reach it.
6. **Documentation.** A "Sending through an OpenTelemetry Collector" section in the Collector README (and a link from
   the top-level README and `docs/SETUP.md`): when to use one (durability across outages, sampling, redaction,
   enrichment, many apps behind one key), the sample config, how keys and rotation work (the key lives in the client's
   Collector config; rotate by updating it and watching the old key's `last_used_at`), what a full or unavailable
   Keryhe collector looks like from that side.

## Tests

- The local end-to-end check (step 4) is manual; record the commands and the result in the Collector README section
  or a short results file beside the sample config.
- Connection age: a `CollectorAuth`-suite test with `TestServer` is unlikely to exercise real connection closes;
  test with Kestrel on a real port (an integration test, no database needed) that a client making repeated exports
  across the age opens a second connection and loses no export.

## Documentation

- Collector README sections as above; `docs/SETUP.md` and the top-level README link to them.
- `docs/CONFIGURATION.md`: `MaxConnectionAgeSeconds`.
- `CLAUDE.md`: a line under "Collector authentication" or "Default ports" pointing at `deploy/otel-collector/`.

## Done when

- An operator can run more than one collector and knows how traffic reaches all of them.
- A client can copy the sample config, set one environment variable, and ship data through their own OTel Collector,
  with outages on our side absorbed by their file queue.
