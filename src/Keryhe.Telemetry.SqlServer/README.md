# Keryhe.Telemetry.SqlServer

SQL Server provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry) —
Microsoft.Data.SqlClient/Dapper implementations of the write and read repositories.

## What it provides

Telemetry data (`Database:Provider = SqlServer`):

- `AddSqlServerCollectorServices(configuration)` — registers `ITelemetryBulkWriter` (SqlBulkCopy/MERGE-based), connecting via `ConnectionStrings:Collector`.
- `AddSqlServerApiServices(configuration)` — registers `ITraceReadRepository`,
  `IMetricReadRepository`, `ILogReadRepository`, and `IRetentionSweeper`, connecting via
  `ConnectionStrings:Api`.

Control plane (`ControlPlane:Provider = SqlServer`), connecting via `ConnectionStrings:ControlPlane`, so it can sit
beside any telemetry provider, including ClickHouse:

- `AddSqlServerControlPlaneCollectorServices(configuration)` — `IApiKeyLookup` and `IApiKeyTouchStore`.
- `AddSqlServerControlPlaneApiServices(configuration)` — `IAlertRuleRepository`,
  `ITenantCatalogRepository`, and `IRetentionSettingsRepository`.

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side).

## Usage

```csharp
// Collector host
builder.Services.AddSqlServerCollectorServices(builder.Configuration);
builder.Services.AddSqlServerControlPlaneCollectorServices(builder.Configuration);

// API host
builder.Services.AddSqlServerApiServices(builder.Configuration);
builder.Services.AddSqlServerControlPlaneApiServices(builder.Configuration);
```

Configuration:

- `Database:Provider` / `ControlPlane:Provider` — `SqlServer` for whichever side this provider serves.
- `ConnectionStrings:Collector` / `ConnectionStrings:Api` / `ConnectionStrings:ControlPlane` — standard ADO.NET connection strings, e.g.
  `Server=localhost;Database=telemetry;User Id=sa;Password=<password>;TrustServerCertificate=true`. `ControlPlane` is required (no fallback) and normally names the same database.

Apply `schema/SqlServer-ControlPlane.sql` and `schema/SqlServer-Telemetry.sql` (or `schema/apply-schema.sh controlplane sqlserver`
and `schema/apply-schema.sh telemetry sqlserver`) before first use.

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture
and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
