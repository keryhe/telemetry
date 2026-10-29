# Provider packages

What each provider package registers. Which one to choose and how to configure it is in [SETUP.md](SETUP.md).

## Core (`Keryhe.Telemetry.Core`)

- Core interfaces implemented once per database provider: `ITelemetryBulkWriter`,
  `ITraceReadRepository`, `IMetricReadRepository`, `ILogReadRepository`,
  `IAlertRuleRepository`, `ITenantCatalogRepository`, `IRetentionSettingsRepository`, and
  `ITenantResolver`.
- Retention: each tenant's windows (`RetentionSettings`, columns on its `tenants` row; no global default) come from
  `IRetentionSettingsRepository`; the sweep takes them as a `RetentionPolicy` (a tenant with no row expires on
  `RetentionDefaults`, 30/30/30) and `IRetentionSweeper` deletes tenant by tenant.
- The domain models passed across those interfaces (traces, metrics, logs, resources,
  instrumentation scopes, alert rules, tenants).
- Provider-agnostic write-path machinery: the bounded `TelemetryIngestionChannel`, the
  `TelemetryIngestionWorker` background service that drains it, and `ResourceScopeCache` for
  in-process resource/scope dedup.
- Shared Dapper plumbing: read repository bases (`Data/Read/*RepositoryBase.cs`) and the JSONB
  attribute type handler (`Data/Dapper/JsonAttributesTypeHandler.cs`) used by every provider.

This package is a dependency of the provider packages (`Keryhe.Telemetry.PostgreSQL`,
`Keryhe.Telemetry.SqlServer`, `Keryhe.Telemetry.MySql`,
`Keryhe.Telemetry.ClickHouse`, `Keryhe.Telemetry.Parquet`) and of `Keryhe.Telemetry.Api`/`Keryhe.Telemetry.Collector`; it is
rarely installed directly — install one of those instead, and this one comes along
transitively.

## PostgreSQL (`Keryhe.Telemetry.PostgreSQL`)

Telemetry data (`Telemetry:{Collector|Api}:Provider:Database = PostgreSQL`):

- `AddPostgreSqlCollectorServices(configuration)` — registers a singleton `NpgsqlDataSource` (from
  `ConnectionStrings:Collector`) and `ITelemetryBulkWriter`.
- `AddPostgreSqlApiServices(configuration)` — registers a singleton `NpgsqlDataSource` (from
  `ConnectionStrings:Api`), `ITraceReadRepository`, `IMetricReadRepository`,
  `ILogReadRepository`, and `IRetentionSweeper`.

Control plane (`Telemetry:{Collector|Api}:Provider:ControlPlane = PostgreSQL`), on its own keyed `NpgsqlDataSource` (from
`ConnectionStrings:ControlPlane`), so it can sit beside any telemetry provider, including ClickHouse:

- `AddPostgreSqlControlPlaneCollectorServices(configuration)` — `IApiKeyLookup` and `IApiKeyTouchStore`.
- `AddPostgreSqlControlPlaneApiServices(configuration)` — `IAlertRuleRepository`,
  `ITenantCatalogRepository`, and `IRetentionSettingsRepository`.

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side).

## SQL Server (`Keryhe.Telemetry.SqlServer`)

Telemetry data (`Telemetry:{Collector|Api}:Provider:Database = SqlServer`):

- `AddSqlServerCollectorServices(configuration)` — registers `ITelemetryBulkWriter` (SqlBulkCopy/MERGE-based), connecting via `ConnectionStrings:Collector`.
- `AddSqlServerApiServices(configuration)` — registers `ITraceReadRepository`,
  `IMetricReadRepository`, `ILogReadRepository`, and `IRetentionSweeper`, connecting via
  `ConnectionStrings:Api`.

Control plane (`Telemetry:{Collector|Api}:Provider:ControlPlane = SqlServer`), connecting via `ConnectionStrings:ControlPlane`, so it can sit
beside any telemetry provider, including ClickHouse:

- `AddSqlServerControlPlaneCollectorServices(configuration)` — `IApiKeyLookup` and `IApiKeyTouchStore`.
- `AddSqlServerControlPlaneApiServices(configuration)` — `IAlertRuleRepository`,
  `ITenantCatalogRepository`, and `IRetentionSettingsRepository`.

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side).

## MySQL (`Keryhe.Telemetry.MySql`)

Telemetry data (`Telemetry:{Collector|Api}:Provider:Database = MySql`):

- `AddMySqlCollectorServices(configuration)` — registers `ITelemetryBulkWriter`, connecting via `ConnectionStrings:Collector`.
- `AddMySqlApiServices(configuration)` — registers `ITraceReadRepository`,
  `IMetricReadRepository`, `ILogReadRepository`, and `IRetentionSweeper`, connecting via
  `ConnectionStrings:Api`.

Control plane (`Telemetry:{Collector|Api}:Provider:ControlPlane = MySql`), connecting via `ConnectionStrings:ControlPlane`, so it can sit
beside any telemetry provider, including ClickHouse:

- `AddMySqlControlPlaneCollectorServices(configuration)` — `IApiKeyLookup` and `IApiKeyTouchStore`.
- `AddMySqlControlPlaneApiServices(configuration)` — `IAlertRuleRepository`,
  `ITenantCatalogRepository`, and `IRetentionSettingsRepository`.

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side).

## ClickHouse (`Keryhe.Telemetry.ClickHouse`)

- `AddClickHouseCollectorServices(configuration)` — registers `ITelemetryBulkWriter` (batched
  `ClickHouseBulkCopy` inserts) and replaces the shared ingestion worker with a ClickHouse one that batches per day,
  connecting via `ConnectionStrings:Collector`.
- `AddClickHouseApiServices(configuration)` — registers the trace, log, metric, resource and rollup read
  repositories and `IRetentionSweeper`, connecting via `ConnectionStrings:Api`.

**ClickHouse holds telemetry data only.** Tenants, API keys, alert rules and each tenant's retention windows (the control
plane) live in PostgreSQL, SQL Server, MySQL or a SQLite file: also reference that provider's package and register its
`Add<Provider>ControlPlaneCollectorServices` / `Add<Provider>ControlPlaneApiServices`
(`Telemetry:{Collector|Api}:Provider:ControlPlane`, `ConnectionStrings:ControlPlane`).

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side), and set both hosts' `Provider:Database` to `ClickHouse`.
