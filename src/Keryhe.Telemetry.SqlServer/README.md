# Keryhe.Telemetry.SqlServer

SQL Server provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry) —
Microsoft.Data.SqlClient/Dapper implementations of the write and read repositories.

## What it provides

- `AddSqlServerCollectorServices(configuration)` — registers `ITelemetryBulkWriter`
  (SqlBulkCopy/MERGE-based) and `ITenantResolver`, connecting via `ConnectionStrings:Collector`.
- `AddSqlServerApiServices(configuration)` — registers `ITraceReadRepository`,
  `IMetricReadRepository`, `ILogReadRepository`, `IAlertRuleRepository`,
  `ITenantCatalogRepository`, and `IRetentionSettingsRepository`, connecting via
  `ConnectionStrings:Api`.

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side), and set `Database:Provider` to `SqlServer`.

## Usage

```csharp
builder.Services.AddSqlServerCollectorServices(builder.Configuration);
builder.Services.AddSqlServerApiServices(builder.Configuration);
```

Configuration:

- `Database:Provider` — must be `SqlServer`.
- `ConnectionStrings:Collector` / `ConnectionStrings:Api` — standard ADO.NET connection strings, e.g.
  `Server=localhost;Database=telemetry;User Id=sa;Password=<password>;TrustServerCertificate=true`.

Apply `schema/SqlServer-Schema.sql` (or `schema/apply-schema.sh sqlserver`) before first use.

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture
and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
