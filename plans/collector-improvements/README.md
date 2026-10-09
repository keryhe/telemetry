# Collector improvements

Written 2026-10-08. Ideas borrowed from the OpenTelemetry Collector (receivers, `memory_limiter`, the exporter's
queue and retry layer) and applied to `Keryhe.Telemetry.Collector`. The core write path (ingestion channel, record
gate, merged batches, concurrent flushes, jittered retry, drain on shutdown) already matches what the OTel Collector
does; these plans close the gaps around it.

## Deployment decision

- **No OTel Collector gateway on our side.** Keryhe's collector stays the only thing clients talk to, so it must
  handle backpressure, bad input and OTLP over HTTP itself.
- **Clients may run their own OTel Collector** (agent or gateway on their side). Their apps send to it with no key;
  its `otlp` exporter adds the tenant's Keryhe key in `headers`. We document this setup (phase 5) and check our key
  as we do today. How their apps authenticate to their own Collector is their concern.
- Clients without a Collector (SDKs exporting directly, the Java agent's `http/protobuf` default) reach us directly,
  which is why input limits (phase 2) and OTLP/HTTP (phase 4) stay on our list.
- **Security model: TLS plus a bearer key per tenant**, as today (TLS is already required outside Development).
  Phase 3 adds what a shared, multi-organisation collector needs on top: per-tenant quotas and limits on failed
  authentication. Client certificates and IP allowlists are not planned until a customer asks.

## Plans, in order

| Phase | Plan | Items | Size |
|---|---|---|---|
| 1 | [phase-1-backpressure-and-failures.md](phase-1-backpressure-and-failures.md) | Reject quickly when full; split failing batches; accepted/refused counters and health endpoints | Medium. Ingestion path, every service, the stress harness |
| 2 | [phase-2-input-limits.md](phase-2-input-limits.md) | Byte budget on the gate and a configured max message size; attribute and field limits | Small to medium. Gate, conversion code |
| 3 | [phase-3-tenant-isolation.md](phase-3-tenant-isolation.md) | Per-tenant share of the gate and optional rate limit; failed-auth limiting per client IP, capped and coalesced key lookups | Medium. Gate, auth handler, resolver, a stress profile |
| 4 | [phase-4-otlp-http.md](phase-4-otlp-http.md) | OTLP over HTTP (protobuf and JSON, gzip) | Large. New endpoints, auth, Kestrel, transport guard |
| 5 | [phase-5-docs-and-deployment.md](phase-5-docs-and-deployment.md) | Load-balancing guidance (and an optional maximum connection age); client-side OTel Collector setup | Small. Mostly documentation and one end-to-end check |

Why this order:
- Phase 1 fixes behaviour under load that is wrong today (exports held open until the client times out; one bad
  record drops a whole merged batch), and gives the later phases a rejection type and counters to report into.
- Phase 2 is small and makes phase 1's permanent-failure path rare (most permanent failures are over-length values).
- Phase 3 builds its per-tenant share on phase 2's two-budget gate and rejects through phase 1's path. It comes before
  more tenants are onboarded, and before phase 4 so the HTTP endpoints get it for free.
- Phase 4 reuses phases 1 to 3's rejection mapping and phase 2's limits; doing it late means the HTTP adapter maps
  finished behaviour instead of chasing it.
- Phase 5's sample client config should quote the limits from phase 2 and the throttling from phases 1 and 3.

Phase 5 is independent of 2 to 4 and can move earlier if documentation is wanted sooner. Phase 3's auth-abuse half
(steps 6 to 8) does not depend on phase 2 and can be pulled forward on its own if the collector is exposed to the
internet before then.

## Deferred: durability of accepted data

Records are acknowledged when they are enqueued, so a crash or OOM kill loses whatever is queued in memory (up to
`MaxQueued*` records per signal), and a batch dropped after its retries is lost too. The OTel Collector answers this
with a file-backed sending queue. Two options, neither scheduled:

1. **Disk spill.** Append each accepted export to a local append-only file before acknowledging, delete segments once
   their batches commit, replay on start. Keeps today's latency; adds disk I/O on every export, a replay path and
   segment housekeeping, and only protects against a process crash, not a lost disk or node.
2. **Acknowledge after commit (opt-in mode).** The export returns only once its records are flushed. Durability then
   rests with the client's own retry or its Collector's file queue. Simple to build (the commit already happens; the
   export waits on a completion), but couples export latency to database latency, which the channel exists to avoid,
   and needs the client to batch well (a client-side Collector does).

Revisit after phase 1's stress run shows how often batches are actually dropped. If clients mostly run their own
Collector, option 2 is the cheaper one.

## Out of scope

- Browser clients over OTLP/HTTP (CORS). A browser would carry the tenant's API key in the page. The collector host's
  current `AllowAnyOrigin` CORS policy should be reviewed separately.
- Tail sampling, redaction, filtering, Kubernetes enrichment: clients who want them run an OTel Collector.
- zstd compression (the OTel Collector accepts it; most SDKs send gzip or nothing).
