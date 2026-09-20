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
- `MapKeryheTelemetryCollector()` — maps the three gRPC services onto the endpoint pipeline.

The host application still owns Kestrel configuration (including h2c for gRPC) and CORS.

## Usage

```csharp
builder.Services.AddKeryheTelemetryCollector(builder.Configuration);

// Install the NuGet package matching your chosen provider alongside this one, e.g.
// Keryhe.Telemetry.PostgreSQL, and call its own Add<Provider>CollectorServices:
builder.Services.AddPostgreSqlCollectorServices(builder.Configuration);

var app = builder.Build();
app.MapKeryheTelemetryCollector();
app.Run();
```

Configuration:

- `ConnectionStrings:Collector` — connection string for the collector (read by the provider's
  own `Add<Provider>CollectorServices` call, not by `AddKeryheTelemetryCollector`).

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture,
ingestion pipeline, and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
