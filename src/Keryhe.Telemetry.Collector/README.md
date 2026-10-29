# Keryhe.Telemetry.Collector

OTLP ingestion (gRPC and HTTP) for [Keryhe Telemetry](https://github.com/keryhe/telemetry): the trace, metric and log
services, per-tenant API key authentication and the write-path wiring. It runs as its own host, never in the same process
as the REST API.

## Usage

```csharp
builder.Services.AddKeryheTelemetryCollector(builder.Configuration);
builder.Services.AddPostgreSqlCollectorServices(builder.Configuration);              // your telemetry provider's package
builder.Services.AddPostgreSqlControlPlaneCollectorServices(builder.Configuration);  // your control-plane provider's package

var app = builder.Build();
app.UseRouting();
app.UseAuthentication();   // after UseRouting
app.UseAuthorization();
app.MapKeryheTelemetryCollector();
app.Run();
```

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Telemetry:Collector:Provider:Database` / `ConnectionStrings:Collector` | (required) | The telemetry database |
| `Telemetry:Collector:Provider:ControlPlane` / `ConnectionStrings:ControlPlane` | (required) | The database holding tenants and API keys |
| `Telemetry:Collector:AllowInsecureTransport` | `false` | Outside Development the collector refuses a plaintext address; set `true` only when TLS ends at a proxy in front |
| `Telemetry:Collector:Authentication:Mode` | `ApiKey` | `FixedTenant` accepts exports with no API key, for one tenant (see below) |
| `Kestrel:Endpoints:Https:Certificate` | development certificate | The TLS certificate (`Path` + password, or `Store`/`Location`/`Subject`) |

Every other setting (queue sizes, limits, tenant quotas, health, rollups) is in [CONFIGURATION.md](../../docs/CONFIGURATION.md#collector).

## Sending data

Each export carries a tenant's API key (create one with the Admin tool, see [SETUP.md](../../docs/SETUP.md#3-create-a-tenant-and-an-api-key)):

```
OTEL_EXPORTER_OTLP_ENDPOINT=https://collector.example.com:7057
OTEL_EXPORTER_OTLP_HEADERS=Authorization=Bearer ktel_<43 characters>
# for OTLP/HTTP add: OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

## Single-tenant install (no API keys)

For an install that serves one customer, the collector can run without keys: every export is stored under one tenant and an
`Authorization` header is ignored. The tenant row is still needed (the API, the UI, retention and alert rules all use it); `api_keys`
stays empty.

1. Create the tenant: the Admin tool's "Create tenant", or `INSERT INTO tenants (name) VALUES ('<name>');` on the control plane. Note its id.
   With a SQLite control plane there is nothing to do: whichever host starts first creates the file, and the collector (and the API host,
   with `Telemetry:Api:FixedTenantId`) creates the tenant, named `Default`, with the configured id when it has no row.
2. Configure the collector:

   ```json
   "Telemetry": { "Collector": { "Authentication": {
     "Mode": "FixedTenant", "FixedTenantId": 1, "AllowUnauthenticatedIngestion": true } } }
   ```

   `AllowUnauthenticatedIngestion` is required outside Development. The collector checks the tenant's row before it listens and refuses to
   start without it (a connection error is retried for 30 s, except on SQLite). It logs a warning at every start.
3. On the API host set `Telemetry:Api:FixedTenantId` to the same id: the UI hides its Tenant selector and every page is that tenant's.
4. Point exporters at the collector with no `OTEL_EXPORTER_OTLP_HEADERS`. The test data generator does this with `--Generator:FixedTenant=true`.

What changes:

- **The network is the boundary.** Anyone who can reach the OTLP endpoints can write telemetry: bind the collector to an internal interface
  and firewall its ports. `AllowInsecureTransport` still governs plaintext addresses.
- **Readiness** no longer depends on the control plane (there are no key lookups); it reflects shutdown and a full queue only.
- `Telemetry:Ingestion:TenantQuota:MaxShare` defaults to `1` (the whole queue), unless set.
- **The tenant is checked at startup only.** A tenant deleted later keeps receiving data; retention expires it on the built-in 30-day windows.
- **Switching to keys later:** create keys with the Admin tool, roll them out to the exporters' headers, then set `Mode` to `ApiKey` (or
  remove it) and restart.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
