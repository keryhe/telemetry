# Keryhe.Telemetry.Timescale

TimescaleDB provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry) — a
hypertable-aware write path on top of Npgsql/Dapper, reusing the
`Keryhe.Telemetry.PostgreSQL` read repositories.

## What it provides

- `AddTimescaleCollectorServices(configuration)` — registers a singleton `NpgsqlDataSource` (from
  `ConnectionStrings:Collector`), `ITelemetryBulkWriter`, and `ITenantResolver`,
  targeting TimescaleDB's hypertables for the metric data-point tables and `log_records`.
- `AddTimescaleApiServices(configuration)` — registers a singleton `NpgsqlDataSource` (from
  `ConnectionStrings:Api`) and the read repositories (including `IRetentionSettingsRepository`),
  derived from the PostgreSQL implementations since the query surface is identical.

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side), and set `Database:Provider` to `Timescale`.

## Usage

```csharp
builder.Services.AddTimescaleCollectorServices(builder.Configuration);
builder.Services.AddTimescaleApiServices(builder.Configuration);
```

Configuration:

- `Database:Provider` — must be `Timescale`.
- `ConnectionStrings:Collector` / `ConnectionStrings:Api` — Npgsql connection strings, e.g.
  `Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>`.
- In the all-in-one host (`Keryhe.Telemetry.Server`), the write and read connection strings must
  be identical — the underlying `NpgsqlDataSource` is a process-wide singleton.

Apply `schema/Timescale-Schema.sql` (or `schema/apply-schema.sh timescale`) before first use. It
sets up hypertables on the metric data-point tables and `log_records`, compression starting at 7
days, retention (metrics 180 days, logs 90 days), and a continuous aggregate
(`log_severity_stats_daily`, refreshed every 5 minutes) behind the `log_severity_stats` view.

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture
and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
