# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

Requires the .NET 10 SDK.

```bash
# Build
dotnet build Telemetry.sln

# Run the gRPC ingestion server
dotnet run --project src/Keryhe.Telemetry.Server

# Run the Blazor UI
dotnet run --project src/Keryhe.Telemetry.Client

# Run test data generator (sends synthetic OTLP data to the server)
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator

# Apply database schema (per provider)
# PostgreSQL (plain):
psql -d telemetry -f schema/PostgreSQL-Schema.sql
# PostgreSQL + TimescaleDB:
psql -d telemetry -f schema/Timescale-Schema.sql
# SqlServer:
sqlcmd -d telemetry -i schema/SqlServer-Schema.sql

# Or use the runner (skips if the target schema_version is already applied):
schema/apply-schema.sh <postgresql|timescale|sqlserver> [database]
```

There is one schema script per supported provider, all producing the same logical
table/column set: `schema/PostgreSQL-Schema.sql` (plain Postgres), `schema/Timescale-Schema.sql`
(Postgres + TimescaleDB hypertables/compression/retention/continuous aggregate), and
`schema/SqlServer-Schema.sql`.

There are no test projects in the solution.

## Architecture

This is an **OpenTelemetry (OTLP) ingestion and visualization platform** — a self-hosted alternative to tools like Jaeger or Grafana Tempo. It receives telemetry via gRPC, stores it in PostgreSQL (TimescaleDB), and visualizes it through a Blazor UI.

### Projects

| Project | Role |
|---------|------|
| `Keryhe.Telemetry.Core` | Domain interfaces and models shared across projects |
| `Keryhe.Telemetry.Data` | EF Core DbContexts and repository implementations |
| `Keryhe.Telemetry.Server` | gRPC server receiving OTLP traces, metrics, and logs |
| `Keryhe.Telemetry.Client` | Blazor Server UI for visualization (MudBlazor + ApexCharts) |
| `Keryhe.Telemetry.Alerting` | Alert rule evaluation with pluggable evaluators and webhook delivery |
| `Keryhe.Telemetry.TestDataGenerator` | Worker service that emits synthetic telemetry via OpenTelemetry SDK |

### Data Flow

```
OpenTelemetry SDKs (any language)
  → OTLP gRPC (port 5117) → Keryhe.Telemetry.Server
  → TelemetryIngestionChannel (bounded channel, capacity 10k per signal type)
  → TelemetryIngestionWorker (background) → TelemetryWriteDbContext → PostgreSQL (TimescaleDB)
  → TelemetryReadDbContext → Keryhe.Telemetry.Client (Blazor UI)
  → AlertEvaluationWorker (background, 60s interval) → AlertDbContext → webhook notifications
```

**Three EF Core DbContexts**: `TelemetryWriteDbContext` (Server writes, full tracking), `TelemetryReadDbContext` (Client reads, no-tracking, global tenant query filter), `AlertDbContext` (alert_rules + alert_events). The main contexts share `TelemetryModelConfiguration` for entity mappings.

### Key Patterns

**Repository pattern with read/write split:**
- Write repos (`TraceWriteRepository`, `MetricWriteRepository`, `LogWriteRepository`) — used by Server
- Read repos (`TraceReadRepository`, `MetricReadRepository`, `LogReadRepository`) — used by Client
- Core interfaces defined in `Keryhe.Telemetry.Core` (e.g. `ITraceWriteRepository`, `ITraceReadRepository`)

**Server gRPC services** (`src/Keryhe.Telemetry.Server/Services/`): Inherit from protobuf-generated base classes, convert OTLP protobuf messages to Core domain models, delegate to write repositories, return partial success responses.

**Client service layer** (`src/Keryhe.Telemetry.Client/Services/`): Interface-based services (`ITraceService`, `IMetricService`, `ILogService`) wrapping read repositories with query/aggregation logic.

**Page state classes** (`src/Keryhe.Telemetry.Client/Services/State/`): Scoped services holding per-page state (filters, selected time range, etc.) shared across Blazor components.

**Client pages** (`Components/Pages/`): Dashboard, Traces, TraceDetail, Metrics, MetricDetail, ServiceMetrics, Logs, Alerts. Each has a corresponding `*PageState` scoped service in `Services/State/`.

**Hash-based deduplication**: Resources and InstrumentationScopes are deduplicated via hash columns (`ResourceHash`, `ScopeHash`) with UNIQUE constraints — inserts use ON CONFLICT DO NOTHING or DO UPDATE. An in-memory `ResourceScopeCache` (singleton `ConcurrentDictionary`) short-circuits DB lookups for resources and scopes already seen in the current process lifetime.

**JSONB for attributes**: OpenTelemetry key-value attributes are stored as JSONB columns (`Attributes`, `FilteredAttributes`) rather than normalized tables.

**Multi-tenant architecture**: All telemetry tables include `tenant_id`. The Server resolves tenants by hashing the `Authorization: Bearer <key>` gRPC header against the `api_keys` table (`ApiKeyTenantResolver`). `ITenantContext` (scoped) carries the active tenant ID. `TelemetryReadDbContext` enforces a global EF Core query filter on `tenant_id`.

**TelemetryIngestionChannel** (Data project, singleton): Three bounded `System.Threading.Channels` (one per signal type, capacity 10,000, `FullMode.Wait`). gRPC service handlers write to the channel; `TelemetryIngestionWorker` drains it in the background. Decouples gRPC latency from DB write latency and provides backpressure.

**Alert evaluation** (`Keryhe.Telemetry.Alerting`): `AlertService.EvaluateAllAsync` iterates all tenants with enabled rules. Each rule type dispatches to a registered `IAlertEvaluator` (`MetricThreshold`, `ErrorRate`, `SlowTrace`, `LogSeveritySpike`). An atomic `TryClaimFireAsync` (UPDATE with cooldown check) prevents duplicate fires under load balancing. `AlertEvaluationWorker` (BackgroundService in Client) drives the loop.

### Database

PostgreSQL + TimescaleDB. Metric data point tables and `log_records` are TimescaleDB hypertables (partitioned on `time_unix_nano`). Compression activates at 7 days; retention drops metrics at 180 days and logs at 90 days. `log_severity_stats_daily` is a TimescaleDB continuous aggregate (refreshes every 5 minutes).

**Telemetry (13)**: `resources`, `instrumentation_scopes`, `spans`, `span_events`, `span_links`, `metrics`, `gauge_data_points`, `sum_data_points`, `histogram_data_points`, `exponential_histogram_data_points`, `summary_data_points`, `exemplars`, `log_records`

**Multi-tenant/auth (2)**: `tenants`, `api_keys`

**Alerting (2)**: `alert_rules`, `alert_events`

**Utility (1)**: `schema_version`

Built-in views: `trace_summary`, `service_map`, `service_map_detailed`, `log_severity_stats` (compatibility alias over the continuous aggregate).

TimescaleDB continuous aggregate: `log_severity_stats_daily`.

Connection strings:
- Server reads from `ConnectionStrings:Write` in `src/Keryhe.Telemetry.Server/appsettings.json`
- Client reads from `ConnectionStrings:Read` in `src/Keryhe.Telemetry.Client/appsettings.json`

Both point to: `Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>`

### Proto Files

OpenTelemetry proto files live in `src/Keryhe.Telemetry.Server/` and are compiled to C# gRPC stubs automatically via MSBuild (`Grpc.Tools`). Covers traces, metrics, logs, profiles, resources, and common types.
