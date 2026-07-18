# Keryhe Telemetry

A self-hosted OpenTelemetry (OTLP) ingestion and visualization platform for traces, metrics, and logs. Provides a gRPC server for receiving telemetry from any OpenTelemetry SDK, a REST API for querying the data, and an Angular UI for visualization.

## Overview

This solution receives OpenTelemetry Protocol (OTLP) data via gRPC, stores it in a database (PostgreSQL, TimescaleDB, SQL Server, MySQL, or ClickHouse), and exposes it through a REST API consumed by an Angular single-page application.

## Features

- **Complete OTLP Support**: Handles traces, metrics, and logs as defined in opentelemetry-proto
- **Angular UI**: Web interface (Angular 20) for exploring traces, metrics, logs, dashboards, and alerts
- **REST API**: ASP.NET Core Web API (`Keryhe.Telemetry.Api`) with Swagger support
- **Normalized Schema**: Efficient storage with proper relationships and hash-based deduplication
- **All Metric Types**: Gauge, Sum, Histogram, Exponential Histogram, and Summary
- **Trace Correlation**: Links logs and metrics to traces via trace and span IDs
- **Built-in Analytics**: Pre-configured views for service maps, trace summaries, and log analysis
- **Multiple Database Providers**: Choose between plain PostgreSQL, PostgreSQL + TimescaleDB, SQL Server, MySQL, or ClickHouse (columnar/OLAP)
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
  → OTLP gRPC (port 5117) → Keryhe.Telemetry.Collector.Server
  → bounded ingestion channel → background worker → provider bulk writer
  → PostgreSQL / TimescaleDB / SQL Server / MySQL / ClickHouse
  → Keryhe.Telemetry.Api.Server (REST API, port 5188 / 7105)
  → Angular SPA (src/telemetry-client, port 4201)
```

The database provider is chosen at runtime via the `Database:Provider` configuration key
(`PostgreSQL`, `Timescale`, `SqlServer`, `MySql`, or `ClickHouse`); each provider ships its own
read/write implementation and is selected by the host at startup. Storage uses Dapper for reads
across all providers (no EF Core); writes use each backend's native bulk path (Npgsql COPY,
`SqlBulkCopy`, MySQL multi-row `INSERT ... ON DUPLICATE KEY UPDATE`, or `ClickHouseBulkCopy`).

| Project | Role |
|---------|------|
| `Keryhe.Telemetry.Core` | Domain interfaces and models shared across projects |
| `Keryhe.Telemetry.Data` | Provider-agnostic write repositories, ingestion channel + background worker, Dapper read-repository bases |
| `Keryhe.Telemetry.PostgreSQL` | Plain-PostgreSQL provider (Npgsql + Dapper): read/write repositories and DI registration |
| `Keryhe.Telemetry.Timescale` | PostgreSQL + TimescaleDB provider (hypertable-aware bulk writes) |
| `Keryhe.Telemetry.SqlServer` | SQL Server provider (Microsoft.Data.SqlClient + Dapper) |
| `Keryhe.Telemetry.MySql` | MySQL provider (MySqlConnector + Dapper): multi-row upsert bulk writes |
| `Keryhe.Telemetry.ClickHouse` | ClickHouse provider (ClickHouse.Client bulk-copy writes + Dapper reads); columnar MergeTree storage |
| `Keryhe.Telemetry.Collector` | gRPC services + OpenTelemetry proto definitions (class library) |
| `Keryhe.Telemetry.Collector.Server` | Thin ASP.NET Core host: maps the gRPC services and runs the ingestion worker |
| `Keryhe.Telemetry.Api` | REST API controllers, tenant middleware, and read-service wiring (class library) |
| `Keryhe.Telemetry.Api.Server` | Thin ASP.NET Core host: composes the API, OpenAPI, CORS, and the alerting worker |
| `Keryhe.Telemetry.Server` | All-in-one host: gRPC ingestion, REST API, and the Angular UI in a single process |
| `Keryhe.Telemetry.Alerting` | Alert rule evaluators, webhook delivery, and the periodic evaluation background worker |
| `Keryhe.Telemetry.TestDataGenerator` | Worker service that emits synthetic telemetry |
| `src/telemetry-client` | Angular 20 SPA (Dashboard, Traces, Metrics, Logs, Alerts) |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (for the Angular client)
- One of the supported database backends:
  - PostgreSQL 14+ (plain or with TimescaleDB extension)
  - SQL Server
  - MySQL 8.0+ (native `JSON` column type)
  - ClickHouse 23.3+ (lightweight `DELETE` support)

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

# MySQL
mysql telemetry < schema/MySQL-Schema.sql

# ClickHouse (creates the database first if needed)
clickhouse-client --database telemetry --multiquery < schema/ClickHouse-Schema.sql
```

Or use the runner script (skips if the target `schema_version` is already applied):

```bash
schema/apply-schema.sh <postgresql|timescale|sqlserver|mysql|clickhouse> [database]
```

**2. Configure connection strings:**

Update `src/Keryhe.Telemetry.Collector.Server/appsettings.json` (gRPC ingestion / write path):
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

Set `Database:Provider` to `PostgreSQL`, `Timescale`, `SqlServer`, `MySql`, or `ClickHouse` to match your chosen backend (both hosts must agree). For SQL Server use a standard ADO.NET connection string. For MySQL use a MySqlConnector connection string, e.g. `Server=localhost;Port=3306;Database=telemetry;User ID=root;Password=<password>`. For ClickHouse use a ClickHouse.Client connection string over the HTTP interface (port 8123), e.g. `Host=localhost;Port=8123;Username=default;Password=<password>;Database=telemetry`.

> **During develoment, connection strings should live in User Secrets, not `appsettings.json`.** Both hosts ship with an
> empty `ConnectionStrings` value and read the real value from .NET User Secrets so credentials
> stay out of source control. Set them once per host:
> ```bash
> dotnet user-secrets --project src/Keryhe.Telemetry.Api.Server \
>   set "ConnectionStrings:Read"  "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
> dotnet user-secrets --project src/Keryhe.Telemetry.Collector.Server \
>   set "ConnectionStrings:Write" "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
> ```
> A local TimescaleDB is easy to run via Docker:
> ```bash
> docker run -d --name timescaledb -p 5432:5432 \
>   -e POSTGRES_PASSWORD=<password> -e POSTGRES_DB=telemetry timescale/timescaledb-ha:pg18
> ```
> Or ClickHouse (the stock image's `default` user is localhost-only, so create a
> network-accessible user for the app):
> ```bash
> docker run -d --name clickhouse -p 8123:8123 -p 9000:9000 \
>   -e CLICKHOUSE_DB=telemetry clickhouse/clickhouse-server
> docker exec clickhouse clickhouse-client -q \
>   "CREATE USER keryhe IDENTIFIED WITH plaintext_password BY '<password>' HOST ANY; \
>    GRANT ALL ON telemetry.* TO keryhe;"
> ```

**3. Configure an API key for the `Authorization` header:**

Ingestion is multi-tenant: the gRPC server resolves the tenant by hashing the
`Authorization: Bearer <key>` header against the `api_keys` table, so every OTLP sender
needs a valid key.

First, create a tenant to attach the key to. On the relational schemas (PostgreSQL,
TimescaleDB, SQL Server, MySQL) the `id` is auto-generated — insert the row and note the
returned `id`:

```sql
INSERT INTO tenants (name) VALUES ('default');
```

ClickHouse has no auto-increment, so supply an explicit `id`:

```sql
INSERT INTO tenants (id, name) VALUES (1, 'default');
```

Then generate a random key and its SHA-256 hash with the helper script in `scripts/`:

```bash
# macOS / Linux
scripts/new-api-key.sh          # or pass a length, e.g. scripts/new-api-key.sh 48

# Windows (PowerShell)
scripts/New-ApiKey.ps1          # or -KeyLength 48
```

The script prints the plaintext **API Key** (store it — it can't be recovered from the hash)
and the **Key Hash** to store in the database. Insert the hash into `api_keys`, pointing it at
an existing tenant (`tenant_id` from the `tenants` table):

```sql
INSERT INTO api_keys (tenant_id, key_hash, name, is_active)
VALUES (<tenant_id>, '<key_hash>', '<key_name>', TRUE);
```

Then include the plaintext key on the `Authorization` header when sending OTLP data. For the
test data generator, set `GeneratorConfig:OtlpHeaders` in
`src/Keryhe.Telemetry.TestDataGenerator/appsettings.json`:

```json
{
  "GeneratorConfig": {
    "OtlpHeaders": "Authorization=Bearer <api_key>"
  }
}
```

Any OpenTelemetry SDK follows the same convention — set the OTLP exporter header
`Authorization=Bearer <api_key>` (e.g. via `OTEL_EXPORTER_OTLP_HEADERS`).

**4. Build:**

```bash
dotnet build Telemetry.sln
```

**5. Run the backend and the client (in separate terminals):**

```bash
# Terminal 1 — all-in-one host (gRPC ingestion + REST API on 5117/7057 and 5188/7105)
dotnet run --project src/Keryhe.Telemetry.Server

# Terminal 2 — Angular dev server
cd src/telemetry-client && npm install && npm run start
```

Or run the two hosts separately instead of the all-in-one:

```bash
# Terminal 1 — gRPC ingestion server
dotnet run --project src/Keryhe.Telemetry.Collector.Server --launch-profile "https"

# Terminal 2 — REST API
dotnet run --project src/Keryhe.Telemetry.Api.Server --launch-profile "https"
```

Open `http://localhost:4201` in your browser.

> The Angular dev server on 4201 is for **development only** — it gives you hot reload and
> talks to the API cross-origin (hence the CORS policy in `Keryhe.Telemetry.Api.Server`).
> For deployment, the API host serves the UI itself; see
> [Deploying](#deploying-a-single-host) below.

**6. (Optional) Generate test data:**

```bash
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator
```

The test data generator sends synthetic traces, metrics, and logs to the server at `http://localhost:5117` on a configurable interval.

## Deploying a Single Host

For a single-node deployment, **`Keryhe.Telemetry.Server`** is the recommended host: it runs gRPC
OTLP ingestion, the REST API, and the compiled Angular UI in one process, so there is nothing else
to deploy.

```bash
dotnet publish src/Keryhe.Telemetry.Server -c Release -o ./publish-server
./publish-server/Keryhe.Telemetry.Server
```

It listens on the same ports as the split hosts — `5117` (h2c) and `7057` (HTTP/2) for OTLP
ingestion, `5188` and `7105` for the API and UI — configured as named Kestrel endpoints in its
`appsettings.json`. It reads **both** `ConnectionStrings:Read` and `ConnectionStrings:Write`.

> **Note.** Under the `PostgreSQL` and `Timescale` providers the read and write connection strings
> must be identical — both sides share a single `NpgsqlDataSource` in one process. The host refuses
> to start otherwise. Use the split hosts below if you need to target separate read/write endpoints.

For scale-out deployments the two hosts can still be run and scaled independently.
`Keryhe.Telemetry.Api.Server` serves the compiled Angular UI alongside the REST API, so it also
needs no separate web server for the SPA and no CORS configuration.

```bash
dotnet publish src/Keryhe.Telemetry.Api.Server -c Release -o ./publish
```

Publishing runs `npm ci && npm run build` in `src/telemetry-client` and stages the output
into the published `wwwroot`. The resulting host serves the UI at `/` and the API under
`/api` on the same origin — deep links like `/traces/<id>` are handled by an SPA fallback
to `index.html`.

- Plain `dotnet build` never invokes npm, so normal .NET builds stay fast.
- Pass `-p:BuildSpaOnPublish=false` to publish against an already-built
  `src/telemetry-client/dist` (useful when CI builds the UI in a separate stage).
- The production build uses `src/environments/environment.prod.ts`, which points `apiUrl`
  at a relative `/api`. The app assumes it is served from the origin root (`<base href="/">`).

When deploying `Keryhe.Telemetry.Api.Server` this way, the gRPC ingestion host
(`Keryhe.Telemetry.Collector.Server`) is deployed separately.

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

When using the MySQL provider (MySQL 8.0+), the schema mirrors the SQL Server relational layout with MySQL-native types (`AUTO_INCREMENT` surrogate keys, `JSON` columns for attributes, `DATETIME(6)` timestamps). Hash-based deduplication of resources/scopes uses `INSERT ... ON DUPLICATE KEY UPDATE`, and writes are batched as multi-row inserts.

When using the ClickHouse provider, tables use the `MergeTree`/`ReplacingMergeTree` family with time-based partitioning. Because ClickHouse has no auto-increment or `RETURNING`, surrogate `id` values are generated by the application (deterministically from the dedup hash for resources/scopes/spans, so `ReplacingMergeTree` collapses re-inserts), and dedup is *eventual* (a merge, or `OPTIMIZE ... FINAL`, resolves duplicates). Control-plane operations are best-effort: alert-rule edits use `ALTER TABLE ... UPDATE` mutations and the cooldown fire-claim is not atomic. See `CLAUDE.md` for the full list of ClickHouse-specific behaviors.

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
