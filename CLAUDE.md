# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

Requires the .NET 10 SDK and Node.js (Angular 20 for the UI).

```bash
# Build the whole .NET solution
dotnet build Telemetry.sln

# Run the gRPC OTLP ingestion server (write path)
dotnet run --project src/Keryhe.Telemetry.Collector.Server

# Run the REST API (read path, consumed by the Angular UI)
dotnet run --project src/Keryhe.Telemetry.Api.Server

# Run the Angular UI (dev server on http://localhost:4201)
cd src/telemetry-client && npm install && npm start

# Run the test data generator (sends synthetic OTLP data to the gRPC server)
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator

# Apply database schema (per provider)
psql -d telemetry -f schema/PostgreSQL-Schema.sql      # PostgreSQL (plain)
psql -d telemetry -f schema/Timescale-Schema.sql       # PostgreSQL + TimescaleDB
sqlcmd -d telemetry -i schema/SqlServer-Schema.sql     # SqlServer
clickhouse-client --database telemetry --multiquery < schema/ClickHouse-Schema.sql  # ClickHouse

# Or use the runner (skips if the target schema_version is already applied):
schema/apply-schema.sh <postgresql|timescale|sqlserver|clickhouse> [database]
```

There is one schema script per supported provider, all producing the same logical
table/column set: `schema/PostgreSQL-Schema.sql` (plain Postgres), `schema/Timescale-Schema.sql`
(Postgres + TimescaleDB hypertables/compression/retention/continuous aggregate),
`schema/SqlServer-Schema.sql`, and `schema/ClickHouse-Schema.sql` (columnar MergeTree family;
see the ClickHouse notes below).

There are no .NET test projects in the solution. The Angular project has `npm test`
(Karma/Jasmine) but no meaningful tests are set up.

### Default ports

- gRPC ingestion (`Keryhe.Telemetry.Collector.Server`): `http://localhost:5117` (h2c), `https://localhost:7057` (HTTP/2)
- REST API (`Keryhe.Telemetry.Api.Server`): `http://localhost:5188`, `https://localhost:7105`
- Angular dev server (`src/telemetry-client`): `http://localhost:4201`

The Angular dev config points at `https://localhost:7105/api` (`src/telemetry-client/src/environments/environment.ts`).

## Architecture

This is an **OpenTelemetry (OTLP) ingestion and visualization platform** — a self-hosted
alternative to tools like Jaeger or Grafana Tempo. It receives telemetry via gRPC, stores it
in a database (PostgreSQL, TimescaleDB, SQL Server, or ClickHouse), and exposes it through a
REST API consumed by an Angular single-page application.

### Projects

| Project | Role |
|---------|------|
| `Keryhe.Telemetry.Core` | Domain interfaces and models shared across projects (no infrastructure deps) |
| `Keryhe.Telemetry.Data` | Provider-agnostic pieces: thin write repositories, ingestion channel + worker, Dapper read repository bases, helpers |
| `Keryhe.Telemetry.PostgreSQL` | Plain-Postgres provider implementation (Npgsql + Dapper) |
| `Keryhe.Telemetry.Timescale` | TimescaleDB provider implementation |
| `Keryhe.Telemetry.SqlServer` | SQL Server provider implementation (Microsoft.Data.SqlClient + Dapper) |
| `Keryhe.Telemetry.ClickHouse` | ClickHouse provider implementation (ClickHouse.Client bulk-copy writes + Dapper reads) |
| `Keryhe.Telemetry.Collector` | gRPC services + OpenTelemetry proto files → generated stubs (class library) |
| `Keryhe.Telemetry.Collector.Server` | Thin ASP.NET Core host that maps the gRPC services and runs the ingestion worker |
| `Keryhe.Telemetry.Api` | REST API controllers, tenant middleware, and read-service wiring (class library) |
| `Keryhe.Telemetry.Api.Server` | Thin ASP.NET Core host that composes the API + OpenAPI + CORS |
| `Keryhe.Telemetry.Alerting` | Alert rule evaluation with pluggable evaluators and webhook delivery |
| `Keryhe.Telemetry.TestDataGenerator` | Worker service that emits synthetic telemetry via the OpenTelemetry SDK |
| `src/telemetry-client` | Angular 20 UI (Angular Material, ApexCharts, ngx-graph) — not part of the .sln |

> The former `Keryhe.Telemetry.Server` (monolithic gRPC host) and `Keryhe.Telemetry.Client`
> (Blazor UI) have been removed. Stale `bin`/`obj` directories may remain on disk but are not
> in the solution. The stack migrated from **EF Core to Dapper** — there are no `DbContext`
> classes anymore.

### Data Flow

```
OpenTelemetry SDKs (any language)
  → OTLP gRPC (port 5117) → Keryhe.Telemetry.Collector (LogService/TraceService/MetricService)
  → thin write repos (Data) enqueue → TelemetryIngestionChannel (bounded, 10k per signal)
  → TelemetryIngestionWorker (background) → ITelemetryBulkWriter (active provider) → DB

Angular UI (localhost:4201)
  → REST (Keryhe.Telemetry.Api.Server, /api) → controllers → I*ReadRepository (active provider, Dapper) → DB
```

### The two composition roots

Both hosts are thin `Program.cs` shells; the real wiring lives in the class libraries:

- **`Keryhe.Telemetry.Collector.Server`** registers the ingestion channel, `ResourceScopeCache`,
  the `TelemetryIngestionWorker`, the write repositories, and the **write-side** provider
  services, then maps the three gRPC services.
- **`Keryhe.Telemetry.Api.Server`** calls `AddKeryheTelemetryApi(configuration)`
  (`TelemetryApiExtensions.cs`), which registers controllers (via an MVC application part,
  since they live in the class library), the tenant context, and the **read-side** provider
  services.

### Provider abstraction (the central pattern)

The database provider is selected at runtime by the **`Database:Provider`** config key
(`"PostgreSQL"`, `"Timescale"`, `"SqlServer"`, or `"ClickHouse"`). Each provider project exposes
`ServiceCollectionExtensions` with `Add<Provider>WriteServices` / `Add<Provider>ReadServices`,
and the hosts `switch` on the config key to call the right one. An unknown/missing provider
throws at startup.

**ClickHouse provider notes.** ClickHouse is columnar/OLAP, so the provider diverges from the
relational three in a few deliberate ways (all confined to the provider; Core interfaces are
unchanged):
- **App-generated ids.** No auto-increment / `RETURNING`. `ClickHouseBulkWriter` computes the
  `Int64` surrogate keys the read repos join on: resource/scope ids are derived deterministically
  from the dedup hash, span ids from `(trace_id, span_id)`, and metrics/events/links use a
  monotonic in-process generator (`ClickHouseIds` / `RowId`).
- **Dedup via `ReplacingMergeTree`, not `ON CONFLICT`.** resources/scopes/spans collapse on their
  `ORDER BY` key at merge time, backed by `ResourceScopeCache` + per-batch dedup. Dedup is
  *eventual* — reads may briefly see a duplicate before a merge (`OPTIMIZE ... FINAL` forces it).
- **Writes go through `ClickHouseBulkCopy`** (async batched insert); reads reuse the shared Dapper
  bases unchanged (attributes are JSON text deserialized in C#; `service.name` uses
  `JSONExtractString`).
- **Deletes** are lightweight `DELETE FROM` with explicit child-row deletes (no FK cascades),
  applied as async mutations.
- **Control-plane is best-effort.** Alert-rule CRUD uses `ALTER TABLE ... UPDATE` mutations and
  `TryClaimFireAsync` is NON-ATOMIC (read-check-then-update), so under concurrent evaluators a
  rule could double-fire. Acceptable because no host currently drives scheduled evaluation.

Core interfaces (in `Keryhe.Telemetry.Core`), each implemented once per provider:

- `ITelemetryBulkWriter` — provider-specific bulk flush driven by the ingestion worker
  (SqlBulkCopy/MERGE for SqlServer; Npgsql binary COPY / `ON CONFLICT` for Postgres).
- `ITelemetryWriteStore` — provider-specific `Delete*` DML for the write path.
- `ITraceReadRepository`, `IMetricReadRepository`, `ILogReadRepository`,
  `IAlertRuleRepository`, `ITenantCatalogRepository` — Dapper read repositories.
- `ITenantResolver` — hashes the `Authorization: Bearer <key>` header against `api_keys`.

Provider projects build a **singleton connection pool** (`NpgsqlDataSource` for Postgres) from
`ConnectionStrings:Write` (ingestion host) or `ConnectionStrings:Read` (API host). Common
Dapper machinery (base repositories, JSONB/attribute type handler) lives in
`Keryhe.Telemetry.Data` (`Read/*RepositoryBase.cs`, `Dapper/JsonAttributesTypeHandler.cs`) and
is shared across providers.

### Key Patterns

**Write path decoupling — `TelemetryIngestionChannel`** (Data, singleton): three bounded
`System.Threading.Channels` (one per signal type, capacity 10,000, `FullMode.Wait`). The thin
write repositories (`TraceWriteRepository`, etc.) just enqueue models and return; deletes are
delegated to `ITelemetryWriteStore`. `TelemetryIngestionWorker` drains the channels and calls
the active provider's `ITelemetryBulkWriter`. This isolates gRPC latency from DB write latency
and provides backpressure.

**gRPC services** (`Keryhe.Telemetry.Collector/Services/`): inherit from protobuf-generated base
classes, convert OTLP protobuf messages to Core domain models, delegate to write repositories,
return partial-success responses.

**REST controllers** (`Keryhe.Telemetry.Api/Controllers/`): `Traces`, `Metrics`, `Logs`,
`Alerts`, `Tenants` — each wraps the corresponding read repository with query/aggregation
logic.

**Hash-based deduplication**: Resources and InstrumentationScopes are deduplicated via hash
columns (`ResourceHash`, `ScopeHash`) with UNIQUE constraints — inserts use ON CONFLICT DO
NOTHING/UPDATE (Postgres) or MERGE (SqlServer). An in-memory `ResourceScopeCache` (singleton
`ConcurrentDictionary`) short-circuits DB lookups for resources/scopes already seen this
process lifetime.

**JSONB for attributes**: OpenTelemetry key-value attributes are stored as JSONB/`nvarchar`
columns (`Attributes`, `FilteredAttributes`) rather than normalized tables; Dapper maps them
via `JsonAttributesTypeHandler`.

**Multi-tenant architecture**: All telemetry tables include `tenant_id`. The ingestion server
resolves tenants by hashing the `Authorization: Bearer <key>` gRPC header against `api_keys`
(`ITenantResolver`). The API resolves the tenant in `TenantMiddleware` and carries it via a
scoped `ITenantContext` (`ApiTenantContext`); read queries filter on `tenant_id`.

**Alerting** (`Keryhe.Telemetry.Alerting`): `AlertService.EvaluateAllAsync` iterates all
tenants with enabled rules, dispatching each rule type to a registered `IAlertEvaluator`
(`MetricThreshold`, `ErrorRate`, `SlowTrace`, `LogSeveritySpike`). An atomic `TryClaimFireAsync`
(UPDATE with cooldown check) prevents duplicate fires under load balancing. The API's
`AlertsController` handles rule CRUD via `IAlertRuleRepository`. Note: no host currently
registers a background worker that drives `EvaluateAllAsync` — evaluation must be invoked
explicitly if you wire it up.

### Database

Providers: plain PostgreSQL, PostgreSQL + TimescaleDB, or SQL Server. Under TimescaleDB, the
metric data-point tables and `log_records` are hypertables (partitioned on `time_unix_nano`);
compression activates at 7 days; retention drops metrics at 180 days and logs at 90 days;
`log_severity_stats_daily` is a continuous aggregate (refreshes every 5 minutes).

**Telemetry (13)**: `resources`, `instrumentation_scopes`, `spans`, `span_events`, `span_links`,
`metrics`, `gauge_data_points`, `sum_data_points`, `histogram_data_points`,
`exponential_histogram_data_points`, `summary_data_points`, `exemplars`, `log_records`

**Multi-tenant/auth (2)**: `tenants`, `api_keys`

**Alerting (2)**: `alert_rules`, `alert_events`

**Utility (1)**: `schema_version`

Built-in views: `trace_summary`, `service_map`, `service_map_detailed`, `log_severity_stats`
(compatibility alias over the continuous aggregate under Timescale).

Connection strings (both hosts point at the same database):
- Ingestion server reads `ConnectionStrings:Write` in `Keryhe.Telemetry.Collector.Server/appsettings.json`
- API server reads `ConnectionStrings:Read` in `Keryhe.Telemetry.Api.Server/appsettings.json`
- Both select the provider via the `Database:Provider` key in the same file

### Proto Files

OpenTelemetry proto files live under `src/Keryhe.Telemetry.Collector/opentelemetry/` and are
compiled to C# gRPC stubs automatically via MSBuild (`Grpc.Tools`). Covers traces, metrics,
logs, profiles, resources, and common types.
