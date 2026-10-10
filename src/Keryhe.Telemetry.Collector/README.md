# Keryhe.Telemetry.Collector

gRPC OTLP ingestion for [Keryhe Telemetry](https://github.com/keryhe/telemetry) — the trace,
metric, and log collector services and provider-agnostic write-path wiring.

## What it provides

- The generated OpenTelemetry proto gRPC service stubs (`LogService`, `TraceService`,
  `MetricService`) and the `Keryhe.Telemetry.Collector.Services` implementations that convert
  OTLP protobuf messages into domain models.
- `AddKeryheTelemetryCollector(configuration)` — registers gRPC, the bounded ingestion channel,
  `ResourceScopeCache`, the `TelemetryIngestionWorker` background service, and the write
  repositories. It does not register a database provider — the host does that separately (see
  Usage below).
- `MapKeryheTelemetryCollector()` — maps the three gRPC services onto the endpoint pipeline and
  requires API key authentication on each (see Authentication below).

The host application still owns Kestrel configuration (including h2c for gRPC) and CORS.

## Usage

```csharp
builder.Services.AddKeryheTelemetryCollector(builder.Configuration);

// Install the NuGet package matching your chosen provider alongside this one, e.g.
// Keryhe.Telemetry.PostgreSQL, and call its own Add<Provider>CollectorServices:
builder.Services.AddPostgreSqlCollectorServices(builder.Configuration);

var app = builder.Build();
app.UseRouting();
app.UseAuthentication();   // after UseRouting: the API key handler acts on the collector's endpoints
app.UseAuthorization();
app.MapKeryheTelemetryCollector();
app.Run();
```

The collector runs as its own service. Co-hosting it in the same process as the REST API is not
supported (the API's `UseAuthentication()` would see the collector's scheme; the handler ignores
non-collector endpoints, but the two have different security boundaries and scaling profiles).

Configuration:

- `ConnectionStrings:Collector` — connection string for the telemetry data (read by the provider's
  own `Add<Provider>CollectorServices` call, not by `AddKeryheTelemetryCollector`).
- `ControlPlane:Provider` and `ConnectionStrings:ControlPlane` — the control-plane database the key lookup and
  `last_used_at` update use (read by `Add<Provider>ControlPlaneCollectorServices`, which throws naming the key when
  the connection string is missing). A control-plane outage longer than the positive cache TTL (30 s) stops
  ingestion for keys that are not cached; the cache and the retryable `UNAVAILABLE` on a failed lookup cover a short one.
- `Telemetry:Collector:AllowInsecureTransport` — see Transport below (default `false`).

## Authentication

Every export is authenticated with a per-tenant API key sent as `Authorization: Bearer <key>`.
`ApiKeyAuthenticationHandler` (scheme `KeryheTelemetryApiKey`) hashes the key (SHA-256, lowercase hex),
resolves it through the provider's `api_keys` table and puts the tenant on the principal; the services read
it from there. A rejected call never reaches a service, so its body is never deserialized, and the response is
a gRPC `UNAUTHENTICATED` (`16`) naming the reason (`missing`, `malformed`, `invalid`, `expired`), or
`UNAVAILABLE` (`14`, retryable) when the key lookup itself failed (database unreachable). OTLP exporters
drop and log `UNAUTHENTICATED`, which is the visible signal that an agent has a wrong or expired key.
Rejections are counted on `keryhe.telemetry.ingestion.auth_failures` (tags `signal`, `reason`); the key is
never logged, only the first 8 hex characters of its hash (the same prefix the Admin TUI shows).

### Security

The model is TLS plus a bearer key per tenant, stated plainly:

- **TLS is required** outside Development (the collector refuses to start on a plaintext address unless TLS is terminated in front of it and
  `AllowInsecureTransport` says so).
- **Keys** are `ktel_` plus 43 base64url characters (about 256 bits), stored only as SHA-256 hashes; an optional expiry; a revoke reaches every
  collector within `PositiveCacheTtlSeconds` (30 s).
- **Failed attempts are limited per client address** (`Telemetry:Collector:AuthFailureLimit`, 5 per second with a burst of 20) so a flood of
  different random keys cannot turn the control-plane database into the target: an address with no attempts left is refused with
  `RESOURCE_EXHAUSTED` before any lookup, and a key already cached as valid is never refused, so a good client behind the same NAT as a
  misconfigured one keeps working. The refusal carries no `RetryInfo`, so an exporter stops instead of hammering. Lookups of uncached keys are
  also capped (`TenantResolution:MaxConcurrentLookups`) and lookups of the same key are coalesced, so a cold start with many clients sharing
  one key makes one query.
- **Behind a reverse proxy the address is the proxy's** unless the host configures ASP.NET Core's forwarded-headers middleware with the proxy
  as a known proxy; the collector does not trust `X-Forwarded-For` by itself. Without it every client behind the proxy shares one bucket.
- **Not offered:** client certificates (mTLS) and IP allowlists. Add them when a customer asks; the natural shape is an optional certificate
  thumbprint or address list per API key in the control plane.

### Fair use between tenants

Tenants share each signal's queue. `Telemetry:Ingestion:TenantQuota:MaxShare` (0.5) caps what one tenant may hold of it, and the optional
`RecordsPerSecond` caps its rate; per-tenant overrides are by tenant id and reload without a restart. A refused export is `UNAVAILABLE`
with `RetryInfo`, exactly like a full queue, but the refusal reason is `tenant_quota` or `tenant_rate`. In the stress harness's `noisy-tenant`
profile (one tenant at ten times the others, on ClickHouse with a small queue) the quiet tenants saw no throttling with the quota and about 5,000
refused attempts each without it. Set `MaxShare` to `1` on a single-tenant installation.

Exporter settings:

```
OTEL_EXPORTER_OTLP_ENDPOINT=https://collector.example.com:7057
OTEL_EXPORTER_OTLP_HEADERS=Authorization=Bearer ktel_<43 characters>
```

### Transport

A key sent in cleartext is compromised the moment it is sent, so outside `Development` the collector
**refuses to start** on a plaintext (`http://`) TCP address. Either terminate TLS on the collector, or
terminate it at a proxy in front and set `Telemetry:Collector:AllowInsecureTransport=true` (the proxy hop
must then be trusted). Unix-socket addresses are exempt. The check reads `Kestrel:Endpoints`, and
`ASPNETCORE_URLS`/`ASPNETCORE_HTTP_PORTS` only when no Kestrel endpoint is configured (Kestrel ignores them
otherwise, so the container image's default `ASPNETCORE_HTTP_PORTS=8080` does not trip it). `Development` adds a plaintext h2c endpoint on
`http://localhost:5117` for the TestDataGenerator and local tools (`appsettings.Development.json`).

The shipped `appsettings.json` has an `Https` endpoint (`https://0.0.0.0:7057`, HTTP/1.1 and HTTP/2 by ALPN) with no
certificate, so Kestrel falls back to the development certificate. Give it a real one in an override
that merges with the shipped endpoint by name, e.g. `appsettings.Production.json`, rather than editing
`appsettings.json`, and keep the password in an environment variable
(`Kestrel__Endpoints__Https__Certificate__Password`):

```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Certificate": { "Path": "/etc/telemetry/collector.pfx" }
      }
    }
  }
}
```

or, from the Windows certificate store:

```json
"Certificate": { "Store": "My", "Location": "LocalMachine", "Subject": "collector.example.com" }
```

### Key format

New keys are `ktel_` followed by 43 base64url characters. The prefix exists so leaked keys can be found
by a secret scanner; a custom pattern is `ktel_[A-Za-z0-9_-]{43}` (GitHub Advanced Security custom
patterns, gitleaks, trufflehog). The collector does not require the prefix: the hash covers the whole
string, so keys issued earlier keep working.

### Issuing, expiring, rotating and revoking keys

The Admin TUI (`Keryhe.Telemetry.Admin`; PostgreSQL, SQL Server and MySQL, the control-plane providers) creates
keys, with an optional expiry (30/90/365 days or a date, stored as UTC), and revokes, reactivates and deletes them.
Keys can also be issued with SQL against the control-plane database: generate the key, compute
`lower(hex(sha256(key)))`, and insert it. `expires_at` is UTC; omit it (NULL) for a key that never expires.

```sql
-- MySQL
INSERT INTO api_keys (tenant_id, key_hash, name, expires_at)
VALUES (1, '<sha256 hex>', 'checkout-agents', UTC_TIMESTAMP(6) + INTERVAL 90 DAY);
```

**Rotation.** Several active keys per tenant are allowed. Create the new key, deploy it to the
exporters, watch the old key's `last_used_at` (shown by the Admin TUI) stop advancing, then revoke it.

**Revocation latency.** A revoked or expired-by-update key keeps working on each collector instance until
its positive cache entry expires (`Telemetry:TenantResolution:PositiveCacheTtlSeconds`, default 30 s), so
revocation takes effect within 30 s; lowering the TTL trades database lookups for latency. There is no
cross-instance invalidation. A key with an `expires_at` stops exactly at that instant regardless of the
cache, because the expiry is compared on every request.

## When the collector is full, and bad records

Exports are acknowledged when they are queued, so the queue is bounded (`Telemetry:Ingestion:MaxQueued*`). When it is full
the collector does not hold the export open until the client's deadline: it waits at most `MaxGateWaitMilliseconds`
(2 s; the day-buffer linger plus 2 s on ClickHouse) and then answers gRPC **`UNAVAILABLE`** with a
`google.rpc.RetryInfo` detail (`grpc-status-details-bin`) of about `RejectRetryDelayMilliseconds` (1 s, plus up to 50% jitter).
Nothing was queued, so nothing acknowledged is lost. `UNAVAILABLE` rather than `RESOURCE_EXHAUSTED` because the OTLP
specification makes the latter retryable only when `RetryInfo` is present. The specification requires a client to throttle
itself on `UNAVAILABLE` and says it should wait `retry_delay`; the OpenTelemetry Collector's `otlp` exporter retries with
the delay it is given. Whether each SDK's exporter honours `RetryInfo` (rather than only retrying `UNAVAILABLE` with its own
backoff) has not been checked against their source: confirm for the SDKs you depend on. A queue that stays full also makes
`/healthz/ready` fail, so a balancer sends traffic elsewhere.

If the database permanently refuses part of a merged batch (a value too long for its column on PostgreSQL, SQL Server or
MySQL), the batch is split until only the offending records are dropped (`records_dropped{reason=permanent}`), and the rest
is stored. Counters on the `Keryhe.Telemetry.Ingestion` meter: `records_accepted` (tags `signal`, `tenant`), `records_refused`
(`signal`, `tenant`, `reason`: `throttled`, `shutting_down`, `invalid`) and `records_dropped` (`signal`, `reason`:
`retries_exhausted`, `permanent`, `split_cap`, `out_of_retention`, `shutdown`).

### OTLP over HTTP

The same endpoint also speaks OTLP/HTTP, for clients that default to it (the Java agent's `http/protobuf`, serverless runtimes, proxies that only
pass HTTP/1.1): `POST /v1/traces`, `/v1/logs` and `/v1/metrics` on the TLS port (`Http1AndHttp2`: ALPN gives a gRPC client HTTP/2 and an HTTP
client HTTP/1.1; in Development a plaintext HTTP/1.1 endpoint is on `http://localhost:5118`). Same key authentication, queue, limits and counters
as gRPC (`records_accepted`/`records_refused` carry a `protocol` tag, `grpc` or `http`).

- **Request:** `Content-Type: application/x-protobuf` or `application/json` (OTLP/JSON: ids are **hex** strings, enums are integers, 64-bit
  integers may be strings; unknown fields are ignored), optionally `Content-Encoding: gzip`. The response uses the request's content type.
- **Status codes:** `200` with the export response (a partial success included, for requests that were readable but not storable); `400`
  for a body that is not an OTLP export; `401` for a missing, malformed, invalid or expired key; `413` for a body over
  `MaxReceiveMessageSizeBytes` once decompressed (a small gzip body cannot expand past it); `415` for another content type or encoding;
  `429` for a client address out of failed attempts (no `Retry-After`: exporters stop); `503` with `Retry-After` when the queue is full, a tenant is
  over its quota or rate, the collector is shutting down or the key lookup is unavailable. Every error body is a `google.rpc.Status`.
- **Exporter settings:** `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` with `OTEL_EXPORTER_OTLP_ENDPOINT=https://collector.example.com:7057` (the SDK
  appends `/v1/traces` etc. to the generic variable; the per-signal variables, such as `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`, take the full path) and
  `OTEL_EXPORTER_OTLP_HEADERS=Authorization=Bearer ktel_...`. In code, the .NET exporter takes the full per-signal URL in `Endpoint`.
- **Behind a TLS-terminating proxy** the gRPC endpoint must be `Http2`-only and OTLP/HTTP needs an HTTP/1.1 endpoint of its own; see `docs/CONFIGURATION.md`.
- Browsers are not supported: there is no CORS policy for these routes, and a browser page would have to carry the tenant's key.

### Size and content limits

An export larger than `Telemetry:Collector:MaxReceiveMessageSizeBytes` (4 MiB, after decompression) is refused with
`RESOURCE_EXHAUSTED`. Within that, nothing is rejected for its content: attribute counts, key and value lengths, event and
link counts, log bodies, nesting and array sizes are capped by `Telemetry:Ingestion:Limits`, and a value stored in a sized
column (span, service, scope and metric names, units, schema URLs, severity text) is clipped to the column. A user can tell
from the record: its dropped-attributes, dropped-events and dropped-links counts include what was cut, and the
`records_truncated` counter (`signal`, `limit`) counts every cut. The queue is also bounded in bytes
(`MaxQueuedBytesPerSignal`, measured on the wire size of the exports), so a few very large exports cannot hold the memory that
many small ones would.

### Health

- **gRPC:** the standard `grpc.health.v1.Health` service on the OTLP endpoints, no API key needed. Use it for Kubernetes gRPC
  probes and gRPC-aware load balancers.
- **HTTP:** `GET /healthz/live` (the process answers) and `GET /healthz/ready` on the plaintext management endpoint
  (`Kestrel:Endpoints:Management`, loopback `5119` as shipped; `Telemetry:Collector:ManagementPort`). Ready fails while the
  collector is shutting down, while API-key lookups have been failing for `ReadinessControlPlaneSeconds`, or while a queue
  has been refusing exports for `ReadinessSaturatedSeconds`. Bind the management endpoint to an address only your
  orchestrator can reach; a public plaintext one fails startup outside Development.

## Running more than one collector

Collectors share nothing but the database, so scaling out is running more instances. The catch is that an OTLP exporter keeps **one HTTP/2
connection open for as long as it runs**: behind a layer-4 (TCP) load balancer each client is pinned to whichever instance it first reached, and an
instance added later gets no traffic until clients restart. Three topologies work:

- **A gRPC-aware (layer 7) balancer or proxy** (Envoy, nginx `grpc_pass`, a cloud application load balancer with HTTP/2 backends) balances each
  request, so the pinning does not happen. Health-check it against `/healthz/ready` on the management port or the `grpc.health.v1` service.
- **Client-side round-robin over DNS:** a Kubernetes headless service (`clusterIP: None`) resolves to every pod, and an exporter that balances
  across addresses (the OpenTelemetry Collector's `otlp` exporter with `balancer_name: round_robin` and a `dns:///` endpoint) uses all of them.
- **A layer-4 balancer plus `Telemetry:Collector:MaxConnectionAgeSeconds`:** a connection older than that (plus up to 10% jitter) is sent an
  HTTP/2 `GOAWAY` after its current request, finishes what is in flight, and the client reconnects through the balancer, to whichever instance it
  picks. Set it to a few minutes; 0 (the default) never closes a healthy connection. It works with clients that reconnect on `GOAWAY`, which every
  gRPC client does. This is tested with a client exporting steadily across several connection lifetimes: no export is lost. A plain layer-4 setup
  without it works but does not rebalance.

An nginx layer-7 example (TLS to the collectors, HTTP/2 both ways):

```
upstream keryhe_collectors { server collector-1:7057; server collector-2:7057; keepalive 64; }
server {
    listen 443 ssl; http2 on;
    ssl_certificate /etc/nginx/tls.crt;  ssl_certificate_key /etc/nginx/tls.key;
    client_max_body_size 8m;
    location / {
        grpc_pass grpcs://keryhe_collectors;
        grpc_read_timeout 60s;
    }
}
```

and a Kubernetes headless service:

```yaml
apiVersion: v1
kind: Service
metadata: { name: keryhe-collector }
spec:
  clusterIP: None          # DNS returns every ready pod
  selector: { app: keryhe-collector }
  ports: [{ name: otlp-grpc, port: 7057 }]
  publishNotReadyAddresses: false   # a pod failing /healthz/ready drops out of DNS
```

with the pods' `readinessProbe` on `/healthz/ready` (HTTP, the management port) or the gRPC probe on the OTLP port.

## Sending through an OpenTelemetry Collector

Your applications do not have to hold a Keryhe key or talk to Keryhe at all. Run an OpenTelemetry Collector (an agent per node or one gateway) on
your side: the applications send OTLP to it with no key, and its `otlp` exporter adds your tenant's key and ships to Keryhe. Reasons to: it
absorbs outages on this side in a file-backed queue (the collector here acknowledges an export when it is queued in memory, so a crash loses what is
queued; a client-side queue is the durable part), it batches below the size limit, and it can sample, redact or enrich before data leaves.

`deploy/otel-collector/client-gateway.yaml` is a tested configuration (needs the contrib distribution for `file_storage`): the `otlp` receiver on
4317/4318, `memory_limiter` then `batch` (batches well under `MaxReceiveMessageSizeBytes`), the `otlp` exporter with the key in `headers`, gzip, retry
that never gives up and a file-backed sending queue. `docker-compose.yaml` runs it next to `telemetrygen` for a local check; the README there records
what was checked and how.

- **The key lives in the client's Collector config** (`KERYHE_API_KEY`). To rotate: issue the new key, put it in the Collector's environment and restart
  (the queue is on disk), watch the old key's `last_used_at` stop moving, then revoke it.
- **What this Collector sees from Keryhe:** a full queue, or a tenant over its share or rate, is `UNAVAILABLE` with `RetryInfo`; the exporter waits as
  told and retries, and the queue absorbs the wait. A key that is invalid, expired or revoked is `UNAUTHENTICATED`, which is permanent: the exporter
  drops the data and logs `Permanent error` with the reason. That log line is the client's signal to fix the key. A failed-attempt limit
  (`AuthFailureLimit`) answers `RESOURCE_EXHAUSTED` without `RetryInfo`, which also stops the retrying.
- **One Collector, several tenants:** an exporter per key (`otlp/tenant-a`, `otlp/tenant-b`) with the `routing` connector choosing by a resource
  attribute. The attribute must come from something this Collector's own receiver authenticates, not from any application that can reach it, or one
  application can write into another tenant's data.

## Summary rollups

Besides the raw spans and logs, the collector maintains a per-minute rollup (`request_rollup_minute`, `log_rollup_minute`,
schema 3.2.0) that the UI's cards and charts and the error-rate and log-spike alerts read. On every provider except
ClickHouse (whose own ingestion worker writes it with each flush, no materialized views) an in-memory accumulator is fed after each successful flush and a
background worker appends the minutes that have closed, every `Telemetry:Rollup:FlushIntervalSeconds` (15) once a
minute is `CloseGraceSeconds` (30) past its end. Three things to know when operating it:

- **A crash loses the rollup of what was in memory** (about a minute and a half) while the spans and logs survive, so a
  chart dips for that slice. A normal stop writes everything, within the host's `ShutdownTimeout` (30 s by default,
  shared with the ingestion drain): give the host more time if it drains large queues.
- **Several collectors** each append their own partial rows; reads sum them, so scale-out needs no coordination.
- The API host reads `FlushIntervalSeconds` and `CloseGraceSeconds` from the same `Telemetry:Rollup` section, so change
  them on both sides. Counters: `rollup_rows_written`, `rollup_rows_dropped`, `rollup_flush_duration` on the
  `Keryhe.Telemetry.Ingestion` meter.

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture,
ingestion pipeline, and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
