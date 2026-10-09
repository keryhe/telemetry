# Phase 3: tenant isolation and auth abuse

Written 2026-10-08. Part of [collector improvements](README.md). After phases 1 and 2: it reuses phase 1's
rejection path and counters, and phase 2's byte budget. Before phase 4, so the OTLP/HTTP endpoints only map these
rejections and need no logic of their own.

TLS plus a bearer key is the standard security model for an OTLP intake and stays the model here. Two gaps matter
once several organisations share one collector:

1. **One tenant can starve the others.** Every signal's gate is shared by all tenants, so a tenant sending more than
   the database can absorb fills it, and every tenant's exports are then rejected (after phase 1) or held open
   (today). Give each tenant a quota.
2. **Unknown keys reach the control-plane database.** The key check runs before the body is read, and
   `CachingTenantResolver` caches failures for 5 s per key hash, but a flood of *different* random keys misses the
   cache every time, and each miss is a control-plane query. A cold start with many clients sharing one valid key also
   sends one query per concurrent request, since nothing coalesces them. Limit failed attempts per client and the
   number of lookups in flight.

## Decisions

| # | Decision |
|---|---|
| 1 | Each tenant may hold at most `Telemetry:Ingestion:TenantQuota:MaxShare` (default 0.5) of each signal's gate, in records and in bytes. A request that would take its tenant over the share is rejected like a full gate (phase 1: `UNAVAILABLE` + `RetryInfo`), with refusal reason `tenant_quota`. A share of 1.0 turns the quota off |
| 2 | Per-tenant overrides in configuration: `Telemetry:Ingestion:TenantQuota:Overrides:<tenantId>:MaxShare`. Not in the control-plane database yet: that is a control-plane schema change (a third-digit bump of the `control-plane` component), worth doing when the Admin tool needs to set it |
| 3 | An optional per-tenant ingest **rate** limit (records per second, token bucket per signal), `Telemetry:Ingestion:TenantQuota:RecordsPerSecond`, default 0 = off, with the same overrides. The share protects the queue; the rate protects the database's throughput. Exceeding it is `UNAVAILABLE` + `RetryInfo` with the time until enough tokens return, reason `tenant_rate` |
| 4 | Failed authentication is limited **per client IP**: a token bucket of `Telemetry:Collector:AuthFailureLimit:PerSecond` (default 5) with a burst of `Burst` (default 20). Only failures (missing, malformed, invalid, expired) take a token. A client with no tokens left is refused **before** any lookup for a key that is not already cached; a key in the positive cache is never refused, so a valid client sharing a NAT with a misconfigured one keeps working |
| 5 | A refused-for-failures request gets gRPC `RESOURCE_EXHAUSTED` **without** `RetryInfo` (OTLP exporters treat that as not retryable, so a misconfigured client stops instead of hammering) and HTTP `429` without `Retry-After` (phase 4). Counted on `auth_failures` with reason `throttled` |
| 6 | Control-plane lookups for uncached keys are capped at `Telemetry:TenantResolution:MaxConcurrentLookups` (default 16). A lookup that cannot start within `LookupQueueTimeoutMilliseconds` (default 1,000) is treated as the existing "lookup unavailable" case (gRPC `UNAVAILABLE`, retryable) and is not cached |
| 7 | Concurrent lookups of the same key hash are coalesced: one query, every waiter gets its result |
| 8 | The client IP is `HttpContext.Connection.RemoteIpAddress`. Behind a proxy that is the proxy's address unless the host configures ASP.NET Core's forwarded-headers middleware with the proxy as a known proxy. The collector does not trust `X-Forwarded-For` by itself; the README explains the setup and what happens without it (every client behind the proxy shares one bucket) |
| 9 | Per-IP state is bounded: `System.Threading.RateLimiting`'s `PartitionedRateLimiter` with idle partitions removed, plus a cap on tracked IPs (`MaxTrackedClients`, default 100,000). When the cap is reached, new IPs share one overflow bucket |

## Steps

### Tenant quotas

1. **Per-tenant accounting.** The gate (records and bytes, after phase 2) keeps a per-tenant resident count beside
   the totals. Acquire checks the tenant's share and the total; release subtracts from both. The tenant and the
   amounts of each export are already carried to the release by the side table on `TelemetryIngestionChannel`
   (phase 2 step 2 adds bytes; add the tenant id). A merged batch releases per tenant.
2. **Options.** `TenantQuotaOptions` (`MaxShare`, `RecordsPerSecond`, `Overrides`), validated at startup (share in
   `(0, 1]`, rate `>= 0`), bound from `Telemetry:Ingestion:TenantQuota`, reloadable through `IOptionsMonitor` so an
   override can change without a restart.
3. **Rate limit.** A `PartitionedRateLimiter` keyed by `(tenant, signal)`, token buckets sized from the options,
   checked in the write repositories before the gate. Skipped entirely when the rate is 0.
4. **Rejections.** Both raise phase 1's `IngestionRejectedException` with their reason; the services already map it.
   `records_refused` gets the two new reasons. Add a `tenant_resident_records` observable gauge (`signal`, `tenant`)
   so an operator can see who is holding the queue.
5. **Stress harness.** A `noisy-tenant` profile: one tenant ramps past the database's ceiling while the others stay at
   a steady rate. Report each tenant's accepted, throttled and export latency. Expected: the steady tenants see no
   throttling and no latency change beyond the shared database cost.

### Auth abuse

6. **Failure limiter.** An `AuthFailureLimiter` singleton (decisions 4, 8, 9). `ApiKeyAuthenticationHandler`: after
   parsing the key and hashing it, if the hash is in the positive cache go on as today; otherwise, if the client's
   bucket is empty, fail with reason `throttled` (decision 5) without a lookup; after a failed resolution, take a
   token. The challenge writes `RESOURCE_EXHAUSTED` for `throttled`.
7. **Resolver.** In `CachingTenantResolver`, expose "is cached" for the handler's check, coalesce concurrent lookups
   per hash (a `ConcurrentDictionary<string, Task<...>>` of in-flight lookups, removed on completion), and gate
   uncached lookups through a `SemaphoreSlim` with the queue timeout (decision 6). The resolver is scoped, so the
   in-flight map and semaphore live in a singleton it depends on.
8. **Options.** `AuthFailureLimit` under `Telemetry:Collector`; `MaxConcurrentLookups` and
   `LookupQueueTimeoutMilliseconds` on `TenantResolutionOptions`.

## Tests

- **Unit:** gate share per tenant (one tenant capped at its share while another still gets in; release per tenant from
  a merged batch; overrides; share 1.0 is today's behaviour); the rate limiter's retry delay; the failure limiter
  (only failures take tokens, cached valid keys bypass, partition cap and overflow bucket).
- **`CollectorAuth` suite (`TestServer`):** with a fake `IApiKeyLookup` counting calls: 1,000 requests with random
  keys from one IP make at most `Burst` + a few lookups and then get `RESOURCE_EXHAUSTED`; a valid cached key from
  the same IP still succeeds; 100 concurrent first requests with one valid key make one lookup; a lookup that blocks
  past the queue timeout gives `UNAVAILABLE`; a tenant over its share gets `UNAVAILABLE` + `RetryInfo` while another
  tenant's export succeeds.
- **Stress:** the `noisy-tenant` profile on PostgreSQL, compared with the same run with `MaxShare` 1.0.

## Documentation

- `docs/CONFIGURATION.md`: `TenantQuota`, `AuthFailureLimit`, the two resolver settings.
- Collector README: a "Security" section stating the model plainly (TLS required outside Development; bearer keys of
  about 256 bits, stored as SHA-256 hashes; expiry and the 30 s revoke; failed-attempt limiting and the forwarded-headers
  setup behind a proxy; what is not offered: client certificates and IP allowlists) and a "Fair use between tenants"
  section (share, rate, overrides, what a throttled client sees).
- `CLAUDE.md`: "Collector authentication" (failure limiting, coalescing, lookup cap) and "Write path decoupling"
  (per-tenant share and rate).

## Not in this phase

- **Client certificates (mTLS) and per-tenant IP allowlists.** Kestrel supports client certificates
  (`ClientCertificateMode`), but issuing and rotating them per client is an operational commitment. Add when a
  customer asks; the natural shape is an optional certificate thumbprint or IP list per API key in the control plane.
- **Mandatory key expiry.** A policy choice for the Admin tool (for example, refuse to create a key without an expiry),
  not collector work.
