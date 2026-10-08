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

The shipped `appsettings.json` has an `Https` endpoint (`https://0.0.0.0:7057`, HTTP/2) with no
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

## Summary rollups

Besides the raw spans and logs, the collector maintains a per-minute rollup (`request_rollup_minute`, `log_rollup_minute`,
schema 3.2.0) that the UI's cards and charts and the error-rate and log-spike alerts read. On every provider except
ClickHouse (whose materialized views maintain it) an in-memory accumulator is fed after each successful flush and a
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
