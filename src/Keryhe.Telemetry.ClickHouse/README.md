# Keryhe.Telemetry.ClickHouse

ClickHouse provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry) — a
ClickHouse.Client/Dapper implementation for columnar/OLAP storage. Requires ClickHouse 23.3+
(lightweight `DELETE`).

## What it provides

- `AddClickHouseCollectorServices(configuration)` — registers `ITelemetryBulkWriter` (batched
  `ClickHouseBulkCopy` inserts), connecting via `ConnectionStrings:Collector`.
- `AddClickHouseApiServices(configuration)` — registers `ITraceReadRepository`,
  `IMetricReadRepository`, `ILogReadRepository`, and `IRetentionSweeper`, connecting via
  `ConnectionStrings:Api`.

**ClickHouse holds telemetry data only.** Tenants, API keys, alert rules and retention settings (the control
plane) live in PostgreSQL, SQL Server or MySQL: also reference that provider's package and register its
`Add<Provider>ControlPlaneCollectorServices` / `Add<Provider>ControlPlaneApiServices`
(`ControlPlane:Provider`, `ConnectionStrings:ControlPlane`).

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side), and set `Database:Provider` to `ClickHouse`.

## Usage

```csharp
// Collector host
builder.Services.AddClickHouseCollectorServices(builder.Configuration);
builder.Services.AddPostgreSqlControlPlaneCollectorServices(builder.Configuration); // or SqlServer / MySql

// API host
builder.Services.AddClickHouseApiServices(builder.Configuration);
builder.Services.AddPostgreSqlControlPlaneApiServices(builder.Configuration);
```

Configuration:

- `Database:Provider` — must be `ClickHouse`.
- `ConnectionStrings:Collector` / `ConnectionStrings:Api` — HTTP-interface connection strings, e.g.
  `Host=localhost;Port=8123;Username=default;Password=<password>;Database=telemetry`.

- `ControlPlane:Provider` — `PostgreSQL`, `SqlServer` or `MySql`; `ConnectionStrings:ControlPlane` — that database
  (both required).

Apply `schema/ClickHouse-Telemetry.sql` (or `schema/apply-schema.sh telemetry clickhouse`) to ClickHouse and the
control-plane script of your control-plane provider to its database, and create a tenant and key with the Admin tool.

Because resources/scopes/spans/metrics dedup via `ReplacingMergeTree` rather than
`ON CONFLICT`/`MERGE`, this provider computes surrogate ids app-side and dedup is *eventual* —
a duplicate row may briefly be visible to reads until the next background merge (or an explicit
`OPTIMIZE ... FINAL`).

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture
and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
