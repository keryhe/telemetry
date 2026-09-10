# Keryhe.Telemetry.MySql

MySQL provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry) —
MySqlConnector/Dapper implementations of the write and read repositories. Requires MySQL 8.0+
(native JSON columns, window functions, CHECK constraints).

## What it provides

- `AddMySqlWriteServices(configuration)` — registers `ITelemetryBulkWriter`, `ITenantResolver`,
  and `ITelemetryWriteStore`, connecting via `ConnectionStrings:Write`.
- `AddMySqlReadServices(configuration)` — registers `ITraceReadRepository`,
  `IMetricReadRepository`, `ILogReadRepository`, `IAlertRuleRepository`, and
  `ITenantCatalogRepository`, connecting via `ConnectionStrings:Read`.

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side), and set `Database:Provider` to `MySql`.

## Usage

```csharp
builder.Services.AddMySqlWriteServices(builder.Configuration);
builder.Services.AddMySqlReadServices(builder.Configuration);
```

Configuration:

- `Database:Provider` — must be `MySql`.
- `ConnectionStrings:Write` / `ConnectionStrings:Read` — MySqlConnector connection strings, e.g.
  `Server=localhost;Port=3306;Database=telemetry;User ID=root;Password=<password>`.

Apply `schema/MySQL-Schema.sql` (or `schema/apply-schema.sh mysql`) before first use.

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture
and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
