# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

Requires the .NET 10 SDK and Node.js (Angular 20 for the UI).

```bash
# Build the whole .NET solution
dotnet build Telemetry.sln

# Run the all-in-one host (gRPC ingestion + REST API + Angular UI in one process)
dotnet run --project src/Keryhe.Telemetry.Server

# ...or run the two split hosts separately (scale-out deployments):
# gRPC OTLP ingestion server (write path)
dotnet run --project src/Keryhe.Telemetry.Collector.Server
# REST API (read path, consumed by the Angular UI)
dotnet run --project src/Keryhe.Telemetry.Api.Server

# Run the Angular UI (dev server on http://localhost:4201 — development only)
cd src/telemetry-client && npm install && npm start

# Publish the all-in-one host (also builds + bundles the Angular UI into wwwroot)
dotnet publish src/Keryhe.Telemetry.Server -c Release -o ./publish-server
# The API-only host publishes the UI the same way:
dotnet publish src/Keryhe.Telemetry.Api.Server -c Release -o ./publish
# ...and to publish against an already-built src/telemetry-client/dist instead:
dotnet publish src/Keryhe.Telemetry.Api.Server -c Release -p:BuildSpaOnPublish=false

# Run the test data generator (sends synthetic OTLP data to the gRPC server)
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator

# Apply database schema (per provider)
psql -d telemetry -f schema/PostgreSQL-Schema.sql      # PostgreSQL (plain)
psql -d telemetry -f schema/Timescale-Schema.sql       # PostgreSQL + TimescaleDB
sqlcmd -d telemetry -i schema/SqlServer-Schema.sql     # SqlServer
clickhouse-client --database telemetry --multiquery < schema/ClickHouse-Schema.sql  # ClickHouse
mysql telemetry < schema/MySQL-Schema.sql               # MySQL

# Or use the runner (skips if the target schema_version is already applied):
schema/apply-schema.sh <postgresql|timescale|sqlserver|clickhouse|mysql> [database]
```

There is one schema script per supported provider, all producing the same logical
table/column set: `schema/PostgreSQL-Schema.sql` (plain Postgres), `schema/Timescale-Schema.sql`
(Postgres + TimescaleDB hypertables/compression/retention/continuous aggregate),
`schema/SqlServer-Schema.sql`, `schema/MySQL-Schema.sql`, and `schema/ClickHouse-Schema.sql`
(columnar MergeTree family; see the ClickHouse notes below). A schema change edits all five plus
`TARGET_VERSION` in `apply-schema.sh`, in one commit.

There are no .NET test projects in the solution. The Angular project has `npm test`
(Karma/Jasmine) but no meaningful tests are set up.

### Default ports

- gRPC ingestion (`Keryhe.Telemetry.Collector.Server`): `http://localhost:5117` (h2c), `https://localhost:7057` (HTTP/2)
- REST API (`Keryhe.Telemetry.Api.Server`): `http://localhost:5188`, `https://localhost:7105` — also serves the UI at `/` when published
- Angular dev server (`src/telemetry-client`): `http://localhost:4201` — **development only**

`Keryhe.Telemetry.Server` (all-in-one) serves all four of the above on the same ports, via named
Kestrel endpoints in its `appsettings.json`: `Grpc` (5117, h2c/`Http2`), `GrpcTls` (7057, `Http2`),
`Api` (5188, `Http1`), `ApiTls` (7105, `Http1AndHttp2`). Its `launchSettings.json` deliberately sets
**no** `applicationUrl` — `ASPNETCORE_URLS` overrides `Kestrel:Endpoints` wholesale and would
collapse the per-endpoint `Protocols`, breaking h2c gRPC on 5117. It also omits `UseHttpsRedirection()`
for the same reason.

The Angular dev config points at `https://localhost:7105/api` (`src/telemetry-client/src/environments/environment.ts`),
which is why the API host has a CORS policy. The **production** build swaps in
`environment.prod.ts` (`apiUrl: '/api'`) via `fileReplacements` in `angular.json`, so a
published deployment is same-origin and needs no CORS.

### UI hosting (published builds)

`Keryhe.Telemetry.Api.Server` serves the SPA from `wwwroot`: `UseDefaultFiles`/`UseStaticFiles`
run before the tenant middleware, and `MapFallbackToFile("index.html")` runs *after*
`MapControllers` so Angular deep links (`/traces/:id`) survive a hard reload while `/api/*`
and `/openapi/*` are never swallowed. The `BuildAngularClient`/`IncludeAngularClient` MSBuild
targets in `Keryhe.Telemetry.Api.Server.csproj` build the SPA and stage `dist/telemetry-client/browser`
into the published `wwwroot` — on publish only, so plain `dotnet build` never runs npm, and
nothing is written into the source tree (there is no checked-in `wwwroot`).

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
| `Keryhe.Telemetry.MySql` | MySQL provider implementation (MySqlConnector + Dapper) |
| `Keryhe.Telemetry.Collector` | gRPC services + OpenTelemetry proto files → generated stubs (class library) |
| `Keryhe.Telemetry.Collector.Server` | Thin ASP.NET Core host that maps the gRPC services and runs the ingestion worker |
| `Keryhe.Telemetry.Api` | REST API controllers, tenant middleware, and read-service wiring (class library) |
| `Keryhe.Telemetry.Api.Server` | Thin ASP.NET Core host that composes the API + OpenAPI + CORS |
| `Keryhe.Telemetry.Server` | All-in-one host: gRPC ingestion + REST API + Angular UI in one process |
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

### The three composition roots

All three hosts are thin `Program.cs` shells; the real wiring lives in the class libraries,
behind two matching extension pairs:

- **Write side** — `AddKeryheTelemetryCollector(configuration)` / `MapKeryheTelemetryCollector()`
  (`Keryhe.Telemetry.Collector/TelemetryCollectorExtensions.cs`): gRPC, the ingestion channel,
  `ResourceScopeCache`, the `TelemetryIngestionWorker`, the write repositories, and the
  **write-side** provider services; then maps the three gRPC services.
- **Read side** — `AddKeryheTelemetryApi(configuration)` / `UseKeryheTelemetryApi()`
  (`Keryhe.Telemetry.Api/TelemetryApiExtensions.cs`): controllers (via an MVC application part,
  since they live in the class library), the tenant context, and the **read-side** provider services.

`Keryhe.Telemetry.Collector.Server` calls the first pair, `Keryhe.Telemetry.Api.Server` the second,
and `Keryhe.Telemetry.Server` calls **both** plus `AddAlerting` and the SPA static-file middleware.

> **All-in-one constraint.** The Npgsql-backed providers (`PostgreSQL`, `Timescale`) register a
> singleton `NpgsqlDataSource` in *both* `Add*WriteServices` (from `ConnectionStrings:Write`) and
> `Add*ReadServices` (from `ConnectionStrings:Read`). In one container the last registration silently
> wins for both paths, so `Keryhe.Telemetry.Server` fails fast at startup
> (`Program.EnsureSingleNpgsqlDataSource`) if the two connection strings differ. SqlServer,
> ClickHouse, and MySql read their connection string per class and are unaffected.

### Provider abstraction (the central pattern)

The database provider is selected at runtime by the **`Database:Provider`** config key
(`"PostgreSQL"`, `"Timescale"`, `"SqlServer"`, `"ClickHouse"`, or `"MySql"`). Each provider project exposes
`ServiceCollectionExtensions` with `Add<Provider>WriteServices` / `Add<Provider>ReadServices`,
and the hosts `switch` on the config key to call the right one. An unknown/missing provider
throws at startup.

**ClickHouse provider notes.** ClickHouse is columnar/OLAP, so the provider diverges from the
relational three in a few deliberate ways (all confined to the provider; Core interfaces are
unchanged):
- **App-generated ids.** No auto-increment / `RETURNING`. `ClickHouseBulkWriter` computes the
  `Int64` surrogate keys the read repos join on, always from the table's full `ORDER BY` key:
  resource ids from `(tenant_id, resource_hash)`, scope ids from `scope_hash` (scopes carry no
  tenant), span ids from `(trace_id, span_id)`, metric ids from
  `(resource_id, scope_id, name, type)` via `ClickHouseIds.FromKey`, and events/links use a
  monotonic in-process generator (`ClickHouseIds` / `RowId`).
- **Dedup via `ReplacingMergeTree`, not `ON CONFLICT`.** resources/scopes/spans/metrics collapse on their
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

**Deduplication of the reference tables**: three entities are deduplicated, by two different
mechanisms, and the distinction is deliberate.

*Hash-keyed* — Resources and InstrumentationScopes dedup on an unbounded attribute map, so they
carry a SHA-256 hash column (`ResourceHash`, `ScopeHash`) with a UNIQUE constraint.

**A resource hash is not a resource identity.** `HashResource` covers only schema URL + attributes,
so two tenants running the same service with the same attributes produce the same hash — which is
why every schema keys resources on `UNIQUE (tenant_id, resource_hash)`, and why anything holding a
resource identity in memory must go through `TelemetryIngestionHelpers.ResourceKey(tenantId, hash)`.
`ResourceScopeCache.TryGetResource`/`SetResource` take the tenant as a parameter for exactly this
reason. Scopes are the deliberate exception: `UNIQUE (scope_hash)` with no tenant, because a scope is
an instrumentation library and is shared across tenants on purpose.

*Natural-keyed* — Metrics dedup on `uk_metric_identity UNIQUE (resource_id, name, type, scope_id)`,
four bounded scalar columns already on the row, so there is no metric hash column. `type` is part
of the key rather than merely refreshed on conflict: the write path picks a data-point table from
the *incoming* type while the read path picks from the *stored* type, so a metric that changed
type and matched an existing row would write points the reader never looks for. Added in schema
2.7.0 — before it, `metrics` grew by one row per export cycle per metric.

Inserts use ON CONFLICT DO UPDATE (Postgres/Timescale), MERGE ... WITH (HOLDLOCK) (SqlServer),
ON DUPLICATE KEY UPDATE (MySql) or `ReplacingMergeTree` (ClickHouse). A singleton
`ResourceScopeCache` (`ConcurrentDictionary`) short-circuits DB lookups for all three, so a warm
process resolves them with no round trip. The cache needs no invalidation because nothing deletes a
resource, scope or metrics catalog row — `ITelemetryWriteStore` offers retention only. If a delete
that removes catalog rows is ever added, it must clear the cache, or every data-point insert fails
its foreign key on each subsequent batch until the process restarts.

Because `metrics.created_at` now means "first seen" rather than approximately the data timestamp,
metric retention prunes `TelemetryIngestionHelpers.TimePrunedMetricTables` — the five data-point
tables plus `exemplars` — on `time_unix_nano`, instead of cascading from `metrics`.

**JSONB for attributes**: OpenTelemetry key-value attributes are stored as JSONB/`nvarchar`
columns (`Attributes`, `FilteredAttributes`) rather than normalized tables; Dapper maps them
via `JsonAttributesTypeHandler`.

**Multi-tenant architecture**: Telemetry is tenant-scoped *through* `resources.tenant_id` — only
`resources`, `api_keys` and `alert_rules` carry a `tenant_id` column, while every signal table
(`spans`, `metrics`, the data-point tables, `log_records`, …) carries just `resource_id` and joins
to reach its tenant. The ingestion server
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

Providers: plain PostgreSQL, PostgreSQL + TimescaleDB, SQL Server, ClickHouse, or MySQL. Under TimescaleDB, the
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
- The all-in-one `Keryhe.Telemetry.Server` reads **both**, and requires them to be identical under
  the Npgsql-backed providers (see the all-in-one constraint above)
- Both select the provider via the `Database:Provider` key in the same file

### Proto Files

OpenTelemetry proto files live under `src/Keryhe.Telemetry.Collector/opentelemetry/` and are
compiled to C# gRPC stubs automatically via MSBuild (`Grpc.Tools`). Covers traces, metrics,
logs, profiles, resources, and common types.
