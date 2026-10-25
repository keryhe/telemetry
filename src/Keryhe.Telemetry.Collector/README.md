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
| `Database:Provider` / `ConnectionStrings:Collector` | (required) | The telemetry database |
| `ControlPlane:Provider` / `ConnectionStrings:ControlPlane` | (required) | The database holding tenants and API keys |
| `Telemetry:Collector:AllowInsecureTransport` | `false` | Outside Development the collector refuses a plaintext address; set `true` only when TLS ends at a proxy in front |
| `Kestrel:Endpoints:Https:Certificate` | development certificate | The TLS certificate (`Path` + password, or `Store`/`Location`/`Subject`) |

Every other setting (queue sizes, limits, tenant quotas, health, rollups) is in [CONFIGURATION.md](../../docs/CONFIGURATION.md#collector).

## Sending data

Each export carries a tenant's API key (create one with the Admin tool, see [SETUP.md](../../docs/SETUP.md#3-create-a-tenant-and-an-api-key)):

```
OTEL_EXPORTER_OTLP_ENDPOINT=https://collector.example.com:7057
OTEL_EXPORTER_OTLP_HEADERS=Authorization=Bearer ktel_<43 characters>
# for OTLP/HTTP add: OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
