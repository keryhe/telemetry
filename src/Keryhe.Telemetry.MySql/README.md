# Keryhe.Telemetry.MySql

MySQL provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry) —
MySqlConnector/Dapper implementations of the write and read repositories. Requires MySQL 8.0+
(native JSON columns, window functions, CHECK constraints).

## What it provides

Telemetry data (`Database:Provider = MySql`):

- `AddMySqlCollectorServices(configuration)` — registers `ITelemetryBulkWriter`, connecting via `ConnectionStrings:Collector`.
- `AddMySqlApiServices(configuration)` — registers `ITraceReadRepository`,
  `IMetricReadRepository`, `ILogReadRepository`, and `IRetentionSweeper`, connecting via
  `ConnectionStrings:Api`.

Control plane (`ControlPlane:Provider = MySql`), connecting via `ConnectionStrings:ControlPlane`, so it can sit
beside any telemetry provider, including ClickHouse:

- `AddMySqlControlPlaneCollectorServices(configuration)` — `IApiKeyLookup` and `IApiKeyTouchStore`.
- `AddMySqlControlPlaneApiServices(configuration)` — `IAlertRuleRepository`,
  `ITenantCatalogRepository`, and `IRetentionSettingsRepository`.

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side).

## Usage

```csharp
// Collector host
builder.Services.AddMySqlCollectorServices(builder.Configuration);
builder.Services.AddMySqlControlPlaneCollectorServices(builder.Configuration);

// API host
builder.Services.AddMySqlApiServices(builder.Configuration);
builder.Services.AddMySqlControlPlaneApiServices(builder.Configuration);
```

Configuration:

- `Database:Provider` / `ControlPlane:Provider` — `MySql` for whichever side this provider serves.
- `ConnectionStrings:Collector` / `ConnectionStrings:Api` / `ConnectionStrings:ControlPlane` — MySqlConnector connection strings, e.g.
  `Server=localhost;Port=3306;Database=telemetry;User ID=root;Password=<password>`. `ControlPlane` is required (no fallback) and normally names the same database.

Apply `schema/MySQL-ControlPlane.sql` and `schema/MySQL-Telemetry.sql` (or `schema/apply-schema.sh controlplane mysql`
and `schema/apply-schema.sh telemetry mysql`) before first use.

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture
and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
