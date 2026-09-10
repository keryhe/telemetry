# Keryhe.Telemetry.Core

Domain interfaces and models for [Keryhe Telemetry](https://github.com/keryhe/telemetry) — the
provider-agnostic contracts shared by every database provider, the ingestion (write) path, and
the read/API path. Has no infrastructure dependencies of its own beyond Dapper's attribute
type handling and the generic `Microsoft.Extensions.*` abstractions.

## What it provides

- Core interfaces implemented once per database provider: `ITelemetryBulkWriter`,
  `ITelemetryWriteStore`, `ITraceReadRepository`, `IMetricReadRepository`,
  `ILogReadRepository`, `IAlertRuleRepository`, `ITenantCatalogRepository`, and
  `ITenantResolver`.
- The domain models passed across those interfaces (traces, metrics, logs, resources,
  instrumentation scopes, alert rules, tenants).
- Provider-agnostic write-path machinery: the bounded `TelemetryIngestionChannel`, the
  `TelemetryIngestionWorker` background service that drains it, and `ResourceScopeCache` for
  in-process resource/scope dedup.
- Shared Dapper plumbing: read repository bases (`Data/Read/*RepositoryBase.cs`) and the JSONB
  attribute type handler (`Data/Dapper/JsonAttributesTypeHandler.cs`) used by every provider.

This package is a dependency of the provider packages (`Keryhe.Telemetry.PostgreSQL`,
`Keryhe.Telemetry.Timescale`, `Keryhe.Telemetry.SqlServer`, `Keryhe.Telemetry.MySql`,
`Keryhe.Telemetry.ClickHouse`) and of `Keryhe.Telemetry.Api`/`Keryhe.Telemetry.Collector`; it is
rarely installed directly — install one of those instead, and this one comes along
transitively.

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture
and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
