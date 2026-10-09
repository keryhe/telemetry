# Keryhe.Telemetry.PostgreSQL

PostgreSQL provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry) — Npgsql/Dapper
implementations of the write and read repositories for a PostgreSQL database, and a PostgreSQL control plane
(tenants, API keys, alert rules, retention settings).

## What it provides

Telemetry data (`Database:Provider = PostgreSQL`):

- `AddPostgreSqlCollectorServices(configuration)` — registers a singleton `NpgsqlDataSource` (from
  `ConnectionStrings:Collector`) and `ITelemetryBulkWriter`.
- `AddPostgreSqlApiServices(configuration)` — registers a singleton `NpgsqlDataSource` (from
  `ConnectionStrings:Api`), `ITraceReadRepository`, `IMetricReadRepository`,
  `ILogReadRepository`, and `IRetentionSweeper`.

Control plane (`ControlPlane:Provider = PostgreSQL`), on its own keyed `NpgsqlDataSource` (from
`ConnectionStrings:ControlPlane`), so it can sit beside any telemetry provider, including ClickHouse:

- `AddPostgreSqlControlPlaneCollectorServices(configuration)` — `IApiKeyLookup` and `IApiKeyTouchStore`.
- `AddPostgreSqlControlPlaneApiServices(configuration)` — `IAlertRuleRepository`,
  `ITenantCatalogRepository`, and `IRetentionSettingsRepository`.

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side).

## Usage

```csharp
// Collector host
builder.Services.AddPostgreSqlCollectorServices(builder.Configuration);
builder.Services.AddPostgreSqlControlPlaneCollectorServices(builder.Configuration);

// API host
builder.Services.AddPostgreSqlApiServices(builder.Configuration);
builder.Services.AddPostgreSqlControlPlaneApiServices(builder.Configuration);
```

Configuration:

- `Database:Provider` / `ControlPlane:Provider` — `PostgreSQL` for whichever side this provider serves.
- `ConnectionStrings:Collector` / `ConnectionStrings:Api` / `ConnectionStrings:ControlPlane` — Npgsql connection strings, e.g.
  `Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>`. `ControlPlane` is required
  (no fallback) and normally names the same database.

Apply `schema/PostgreSQL-ControlPlane.sql` and `schema/PostgreSQL-Telemetry.sql` (or `schema/apply-schema.sh controlplane postgresql`
and `schema/apply-schema.sh telemetry postgresql`) before first use.

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture
and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
