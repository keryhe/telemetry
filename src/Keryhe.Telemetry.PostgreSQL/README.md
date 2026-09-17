# Keryhe.Telemetry.PostgreSQL

PostgreSQL provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry) — Npgsql/Dapper
implementations of the write and read repositories for a plain PostgreSQL database.

## What it provides

- `AddPostgreSqlCollectorServices(configuration)` — registers a singleton `NpgsqlDataSource` (from
  `ConnectionStrings:Collector`), `ITelemetryBulkWriter`, and `ITenantResolver`.
- `AddPostgreSqlApiServices(configuration)` — registers a singleton `NpgsqlDataSource` (from
  `ConnectionStrings:Api`), `ITraceReadRepository`, `IMetricReadRepository`,
  `ILogReadRepository`, `IAlertRuleRepository`, `ITenantCatalogRepository`, and
  `IRetentionSettingsRepository`.

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side), and set `Database:Provider` to `PostgreSQL`.

## Usage

```csharp
builder.Services.AddPostgreSqlCollectorServices(builder.Configuration);
builder.Services.AddPostgreSqlApiServices(builder.Configuration);
```

Configuration:

- `Database:Provider` — must be `PostgreSQL`.
- `ConnectionStrings:Collector` / `ConnectionStrings:Api` — Npgsql connection strings, e.g.
  `Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>`.

Apply `schema/PostgreSQL-Schema.sql` (or `schema/apply-schema.sh postgresql`) before first use.

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture
and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
