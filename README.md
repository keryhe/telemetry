# Keryhe Telemetry

A self-hosted OpenTelemetry (OTLP) ingestion and visualization platform for traces, metrics, and logs. Provides a gRPC server for receiving telemetry from any OpenTelemetry SDK, a REST API for querying the data, and an Angular UI for visualization.

## Overview

This solution receives OpenTelemetry Protocol (OTLP) data via gRPC, stores it in a relational database (PostgreSQL, TimescaleDB, or SQL Server), and exposes it through a REST API consumed by an Angular single-page application.

## Features

- **Complete OTLP Support**: Handles traces, metrics, and logs as defined in opentelemetry-proto
- **Angular UI**: Web interface (Angular 20) for exploring traces, metrics, logs, dashboards, and alerts
- **REST API**: ASP.NET Core Web API (`Keryhe.Telemetry.Api`) with Swagger support
- **Normalized Schema**: Efficient storage with proper relationships and hash-based deduplication
- **All Metric Types**: Gauge, Sum, Histogram, Exponential Histogram, and Summary
- **Trace Correlation**: Links logs and metrics to traces via trace and span IDs
- **Built-in Analytics**: Pre-configured views for service maps, trace summaries, and log analysis
- **Multiple Database Providers**: Choose between plain PostgreSQL, PostgreSQL + TimescaleDB, or SQL Server
- **Alerting**: Rule-based alerts (metric threshold, error rate, slow traces, log severity spikes) with configurable cooldowns and webhook delivery

## Supported Signal Types

### Traces
- Spans with complete context (trace ID, span ID, parent relationships)
- Span events and links
- W3C trace state support
- Status codes and messages

### Metrics
- Gauge data points
- Sum data points (delta and cumulative)
- Histogram data points
- Exponential histogram data points
- Summary data points
- Exemplars with trace correlation

### Logs
- Structured log records
- Severity levels (1-24)
- Multiple body types (string, bool, int, double, bytes, array, kvlist)
- Trace and span correlation

## Architecture

```
OpenTelemetry SDKs (any language)
  → OTLP gRPC (port 5117) → Keryhe.Telemetry.Proto.Server
  → bounded ingestion channel → background worker → Dapper bulk writer
  → PostgreSQL / TimescaleDB / SQL Server
  → Keryhe.Telemetry.Api.Server (REST API, port 5188 / 7105)
  → Angular SPA (src/telemetry-client, port 4201)
```

The database provider is chosen at runtime via the `Database:Provider` configuration key
(`PostgreSQL`, `Timescale`, or `SqlServer`); each provider ships its own read/write
implementation and is selected by the host at startup. Storage uses Dapper (no EF Core).

| Project | Role |
|---------|------|
| `Keryhe.Telemetry.Core` | Domain interfaces and models shared across projects |
| `Keryhe.Telemetry.Data` | Provider-agnostic write repositories, ingestion channel + background worker, Dapper read-repository bases |
| `Keryhe.Telemetry.PostgreSQL` | Plain-PostgreSQL provider (Npgsql + Dapper): read/write repositories and DI registration |
| `Keryhe.Telemetry.Timescale` | PostgreSQL + TimescaleDB provider (hypertable-aware bulk writes) |
| `Keryhe.Telemetry.SqlServer` | SQL Server provider (Microsoft.Data.SqlClient + Dapper) |
| `Keryhe.Telemetry.Proto` | gRPC services + OpenTelemetry proto definitions (class library) |
| `Keryhe.Telemetry.Proto.Server` | Thin ASP.NET Core host: maps the gRPC services and runs the ingestion worker |
| `Keryhe.Telemetry.Api` | REST API controllers, tenant middleware, and read-service wiring (class library) |
| `Keryhe.Telemetry.Api.Server` | Thin ASP.NET Core host: composes the API, OpenAPI, CORS, and the alerting worker |
| `Keryhe.Telemetry.Alerting` | Alert rule evaluators, webhook delivery, and the periodic evaluation background worker |
| `Keryhe.Telemetry.TestDataGenerator` | Worker service that emits synthetic telemetry |
| `src/telemetry-client` | Angular 20 SPA (Dashboard, Traces, Metrics, Logs, Alerts) |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (for the Angular client)
- One of the supported database backends:
  - PostgreSQL 14+ (plain or with TimescaleDB extension)
  - SQL Server

## Setup

**1. Create the database and apply the schema:**

Choose the schema file that matches your database provider:

```bash
createdb telemetry

# Plain PostgreSQL
psql -d telemetry -f schema/PostgreSQL-Schema.sql

# PostgreSQL + TimescaleDB (TimescaleDB extension must be installed first)
psql -d telemetry -f schema/Timescale-Schema.sql

# SQL Server
sqlcmd -d telemetry -i schema/SqlServer-Schema.sql
```

Or use the runner script (skips if the target `schema_version` is already applied):

```bash
schema/apply-schema.sh <postgresql|timescale|sqlserver> [database]
```

**2. Configure connection strings:**

Update `src/Keryhe.Telemetry.Proto.Server/appsettings.json` (gRPC ingestion / write path):
```json
{
  "Database": {
    "Provider": "Timescale"
  },
  "ConnectionStrings": {
    "Write": "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
  }
}
```

Update `src/Keryhe.Telemetry.Api.Server/appsettings.json` (REST API / read path):
```json
{
  "Database": {
    "Provider": "Timescale"
  },
  "ConnectionStrings": {
    "Read": "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
  }
}
```

Set `Database:Provider` to `PostgreSQL`, `Timescale`, or `SqlServer` to match your chosen backend (both hosts must agree). For SQL Server use a standard ADO.NET connection string.

> **Connection strings live in User Secrets, not `appsettings.json`.** Both hosts ship with an
> empty `ConnectionStrings` value and read the real value from .NET User Secrets so credentials
> stay out of source control. Set them once per host:
> ```bash
> dotnet user-secrets --project src/Keryhe.Telemetry.Api.Server \
>   set "ConnectionStrings:Read"  "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
> dotnet user-secrets --project src/Keryhe.Telemetry.Proto.Server \
>   set "ConnectionStrings:Write" "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
> ```
> A local TimescaleDB is easy to run via Docker:
> ```bash
> docker run -d --name timescaledb -p 5432:5432 \
>   -e POSTGRES_PASSWORD=<password> -e POSTGRES_DB=telemetry timescale/timescaledb-ha:pg18
> ```

**3. Build:**

```bash
dotnet build Telemetry.sln
```

**4. Run the server, API, and client (in separate terminals):**

```bash
# Terminal 1 — gRPC ingestion server
dotnet run --project src/Keryhe.Telemetry.Proto.Server

# Terminal 2 — REST API
dotnet run --project src/Keryhe.Telemetry.Api.Server

# Terminal 3 — Angular dev server
cd src/telemetry-client && npm install && npm start
```

Open `http://localhost:4201` in your browser.

**5. (Optional) Generate test data:**

```bash
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator
```

The test data generator sends synthetic traces, metrics, and logs to the server at `http://localhost:5117` on a configurable interval.

### Running the backend for the Angular UI (local verification)

To exercise a UI change end-to-end you only need the **REST API** (read path) plus the Angular
dev server — the gRPC ingestion host is only required when generating new data.

```bash
# Terminal 1 — REST API on https://localhost:7105 (+ http://localhost:5188)
dotnet run --project src/Keryhe.Telemetry.Api.Server --launch-profile https

# Terminal 2 — Angular dev server on http://localhost:4201
cd src/telemetry-client && npm start
```

The Angular dev configuration (`src/telemetry-client/src/environments/environment.ts`) points at
`https://localhost:7105/api`, so the API **must** be run with the `https` launch profile. Open
`http://localhost:4201`.

**Tenant scoping.** The read API resolves the active tenant from an `X-Tenant-Id` request header.
The Angular app sends it automatically (it auto-selects the first tenant), so no manual step is
needed in the browser. When calling the API directly (e.g. `curl` while verifying), pass the
header yourself and `-k` for the dev certificate:

```bash
curl -k -H "X-Tenant-Id: 1" \
  "https://localhost:7105/api/logs/search?start=2026-01-01T00:00:00Z&end=2026-12-31T00:00:00Z&limit=100&offset=0"
```

**Row-count parity.** To confirm a read/query change returns the right rows, compare the API
response against the database directly. With the Docker TimescaleDB above:

```bash
docker exec timescaledb psql -U postgres -d telemetry -tAc \
  "select count(*) from log_records lr join resources r on lr.resource_id=r.id where r.tenant_id=1;"
```

The `search` endpoints (`/api/logs/search`, `/api/traces/search`) return a
`{ items, total, capped }` envelope, so the `total` field can be checked against a `COUNT(*)`
of the same filter in SQL.

## Configuring an OpenTelemetry SDK

Point your OTLP exporter at the server's gRPC endpoint:

```
http://localhost:5117
```

The server accepts gRPC (HTTP/2) connections on this port for all OTLP signal types.

The server requires an `Authorization` gRPC metadata header with a valid API key. Insert a row into the `api_keys` table (SHA-256 hex hash of the key, linked to a tenant) and pass it as `Authorization: Bearer <key>` in your OTLP exporter headers.

## Database Schema

### Key Tables

- `resources` — Entities producing telemetry (services, hosts, etc.)
- `instrumentation_scopes` — Library/instrumentation information
- `spans` — Trace span data
- `span_events`, `span_links` — Span child records
- `metrics` — Base metric metadata
- `gauge_data_points`, `sum_data_points`, `histogram_data_points`, `exponential_histogram_data_points`, `summary_data_points` — Type-specific metric data
- `exemplars` — Metric exemplars with trace correlation
- `log_records` — Log entries with severity and trace correlation
- `tenants` — Tenant registry (a `default` tenant is seeded on first run)
- `api_keys` — Hashed API keys scoped to a tenant, used for ingestion auth
- `alert_rules` — Alert rule definitions (type, condition JSON, webhook URL, cooldown)
- `alert_events` — Audit log of all fired alert events

When using the TimescaleDB provider, metric data point tables and `log_records` are hypertables (partitioned on `time_unix_nano`). Compression activates at 7 days; retention drops metrics at 180 days and logs at 90 days. A continuous aggregate (`log_severity_stats_daily`) refreshes every 5 minutes.

### Built-in Views

- `trace_summary` — Aggregated trace information
- `service_map` — Service-to-service relationships
- `service_map_detailed` — Service map with performance metrics
- `log_severity_stats` — Log severity distribution (compatibility alias)
- `log_severity_stats_daily` — Daily severity counts (TimescaleDB continuous aggregate)

## Alerting

Alert rules are managed through the **Alerts** page in the UI. Each rule specifies:

- **Type**: `MetricThreshold`, `ErrorRate`, `SlowTrace`, or `LogSeveritySpike`
- **Service** (optional): scopes the rule to a single service
- **Condition**: JSON-encoded parameters specific to the rule type
- **Webhook URL**: receives an HTTP POST payload when the rule fires
- **Cooldown**: minimum minutes between repeat firings of the same rule

Rules are evaluated by a background worker (`AlertEvaluationWorker`) hosted in
`Keryhe.Telemetry.Api.Server`. It runs every `AlertEvaluation:IntervalSeconds` (default `60`),
iterating all tenants with enabled rules and dispatching each to its evaluator. An atomic
fire-claim guards the cooldown so a rule fires once even across multiple instances. Configure
it in `src/Keryhe.Telemetry.Api.Server/appsettings.json`:

```json
{
  "AlertEvaluation": {
    "IntervalSeconds": 60,
    "Enabled": true
  }
}
```

Set `Enabled` to `false` to keep the alert API and rule storage available while disabling
automatic evaluation.

## License

[MIT]

## Acknowledgments

Built according to the [OpenTelemetry Protocol Specification](https://github.com/open-telemetry/opentelemetry-proto)
