# Keryhe.Telemetry.ClickHouse

ClickHouse provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry) — a
ClickHouse.Client/Dapper implementation for columnar/OLAP storage. Requires ClickHouse 23.3+
(lightweight `DELETE`).

## What it provides

- `AddClickHouseCollectorServices(configuration)` — registers `ITelemetryBulkWriter` (batched
  `ClickHouseBulkCopy` inserts) and `ITenantResolver`, connecting via `ConnectionStrings:Collector`.
- `AddClickHouseApiServices(configuration)` — registers `ITraceReadRepository`,
  `IMetricReadRepository`, `ILogReadRepository`, `IAlertRuleRepository`,
  `ITenantCatalogRepository`, and `IRetentionSettingsRepository`, connecting via
  `ConnectionStrings:Api`.

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side), and set `Database:Provider` to `ClickHouse`.

## Usage

```csharp
builder.Services.AddClickHouseCollectorServices(builder.Configuration);
builder.Services.AddClickHouseApiServices(builder.Configuration);
```

Configuration:

- `Database:Provider` — must be `ClickHouse`.
- `ConnectionStrings:Collector` / `ConnectionStrings:Api` — HTTP-interface connection strings, e.g.
  `Host=localhost;Port=8123;Username=default;Password=<password>;Database=telemetry`.

Apply `schema/ClickHouse-Schema.sql` (or `schema/apply-schema.sh clickhouse`) before first use,
and seed at least one tenant with an explicit id, e.g.
`INSERT INTO tenants (id, name) VALUES (1, 'default')`.

Because resources/scopes/spans/metrics dedup via `ReplacingMergeTree` rather than
`ON CONFLICT`/`MERGE`, this provider computes surrogate ids app-side and dedup is *eventual* —
a duplicate row may briefly be visible to reads until the next background merge (or an explicit
`OPTIMIZE ... FINAL`). Alert-rule CRUD and cooldown fire-claims use `ALTER TABLE ... UPDATE`
mutations and are best-effort (non-atomic under concurrent evaluators).

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture
and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
