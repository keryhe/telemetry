# Setup

Full setup instructions for every supported database provider, deployment mode, and the
alerting and retention subsystems. See the [README](../README.md) for a quick start with PostgreSQL, and
[CONFIGURATION.md](CONFIGURATION.md) for every configuration key and its default.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (for the Angular client dev server; a publish of the API host also builds the UI with it)
- One of the supported database backends:
  - PostgreSQL 14+ (plain or with the TimescaleDB extension)
  - SQL Server 2022
  - MySQL 8.0.19+ (MariaDB is not supported)
  - ClickHouse 23.3+
- Docker, only to run the integration and stress tests or the Docker recipes below

## 1. Create the database and apply the schema

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

Or use the runner script (skips if the target `schema_version`, currently 3.1.0, is already recorded):

```bash
schema/apply-schema.sh <postgresql|timescale|sqlserver|mysql|clickhouse> [database]
```

> **Schema 3.x is a fresh-install schema.** There is no migration from 2.x: an existing 2.x database must be
> recreated. Within 3.x, `CLAUDE.md` lists the one-statement upgrades (3.0.0 to 3.0.1 to 3.1.0).

## 2. Configure connection strings

Update `src/Keryhe.Telemetry.Collector.Server/appsettings.json` (gRPC ingestion / write path):
```json
{
  "Database": {
    "Provider": "Timescale"
  },
  "ConnectionStrings": {
    "Collector": "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
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
    "Api": "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
  }
}
```

Set `Database:Provider` to `PostgreSQL`, `Timescale`, `SqlServer`, `MySql`, or `ClickHouse` to match your chosen backend (both hosts must agree). For SQL Server use a standard ADO.NET connection string. For MySQL use a MySqlConnector connection string, e.g. `Server=localhost;Port=3306;Database=telemetry;User ID=root;Password=<password>`. For ClickHouse use a ClickHouse.Client connection string over the HTTP interface (port 8123), e.g. `Host=localhost;Port=8123;Username=default;Password=<password>;Database=telemetry`.

> **During development, connection strings should live in User Secrets, not `appsettings.json`.** Both hosts ship with an
> empty `ConnectionStrings` value and read the real value from .NET User Secrets so credentials
> stay out of source control. Set them once per host:
> ```bash
> dotnet user-secrets --project src/Keryhe.Telemetry.Api.Server \
>   set "ConnectionStrings:Api"  "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
> dotnet user-secrets --project src/Keryhe.Telemetry.Collector.Server \
>   set "ConnectionStrings:Collector" "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
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

## 3. Create a tenant and an API key

Ingestion is multi-tenant: the collector resolves the tenant by hashing the
`Authorization: Bearer <key>` header against the `api_keys` table, so every OTLP sender
needs a valid key. **No tenant or key is seeded**; create them before sending data.

### With the Admin tool (PostgreSQL, Timescale, SQL Server)

```bash
dotnet user-secrets --project src/Keryhe.Telemetry.Admin \
  set "ConnectionStrings:Admin" "<connection string>"
dotnet run --project src/Keryhe.Telemetry.Admin
```

Its `appsettings.json` carries `Database:Provider` (set it to your provider). The menus create a tenant, create
an API key (optionally expiring in 30, 90 or 365 days or on a date, UTC), list keys, activate/deactivate and delete
them. The plaintext key (`ktel_` plus 43 characters) is shown once; only its SHA-256 hash is stored. MySQL and
ClickHouse are not supported by the tool; use SQL as below.

### With SQL (any provider)

Create a tenant. On the relational schemas (PostgreSQL, TimescaleDB, SQL Server, MySQL) the `id` is
auto-generated:

```sql
INSERT INTO tenants (name) VALUES ('default');
```

ClickHouse has no auto-increment, so supply an explicit `id`:

```sql
INSERT INTO tenants (id, name) VALUES (1, 'default');
```

Generate a random key and its SHA-256 hash with the helper script in `scripts/`:

```bash
# macOS / Linux
scripts/new-api-key.sh          # or pass a length, e.g. scripts/new-api-key.sh 48

# Windows (PowerShell)
scripts/New-ApiKey.ps1          # or -KeyLength 48
```

The script prints the plaintext **API Key** (store it; it can't be recovered from the hash) and the **Key Hash**
(lowercase-hex SHA-256) to store in the database. Insert the hash into `api_keys`, pointing it at a tenant. The
optional `expires_at` column is UTC; leave it out for a key that never expires:

```sql
INSERT INTO api_keys (tenant_id, key_hash, name, is_active)
VALUES (<tenant_id>, '<key_hash>', '<key_name>', TRUE);
```

ClickHouse's `api_keys` also needs an explicit `id`. To revoke a key set `is_active` to false (on ClickHouse,
`ALTER TABLE api_keys UPDATE is_active = 0 WHERE ... SETTINGS mutations_sync = 1`); a running collector honors it
within 30 seconds (`Telemetry:TenantResolution:PositiveCacheTtlSeconds`).

### Sending data

Any OpenTelemetry SDK follows the same convention: set the OTLP exporter header
`Authorization=Bearer <api_key>` (e.g. via `OTEL_EXPORTER_OTLP_HEADERS`). The test data generator takes one key
per tenant, set in User Secrets (see step 6), not `appsettings.json`.

## 4. Build

```bash
dotnet build Telemetry.sln
```

## 5. Run the backend and the client (in separate terminals)

```bash
# Terminal 1 - gRPC OTLP ingestion (Development: plaintext h2c http://localhost:5117 plus https://localhost:7057)
dotnet run --project src/Keryhe.Telemetry.Collector.Server

# Terminal 2 - REST API + UI (http://localhost:5188, https://localhost:7105)
dotnet run --project src/Keryhe.Telemetry.Api.Server

# Terminal 3 - Angular dev server
cd src/telemetry-client && npm install && npm run start
```

Open `http://localhost:4201` in your browser.

> The Angular dev server on 4201 is for **development only**: it gives you hot reload and
> talks to the API cross-origin (hence the CORS policy in `Keryhe.Telemetry.Api.Server`).
> The API host also serves the prebuilt UI itself, even under `dotnet run`; as shipped, its
> `appsettings.json` mounts the UI at `/telemetry` and the API at `/telemetry/api`
> (`TelemetryUi:BasePath`, `Telemetry:Api:BasePath`; the defaults when unset are `/` and `/api`).
> See [Deploying](#deploying) below.

## 6. (Optional) Generate test data

The generator simulates an e-commerce system for each tenant in `Generator:Tenants` and sends it to the
collector. The tenants' API keys must already exist in the database (step 3); put each in User Secrets:

```bash
dotnet user-secrets --project src/Keryhe.Telemetry.TestDataGenerator \
  set "Generator:Tenants:0:ApiKey" "<key for acme-retail>"
dotnet user-secrets --project src/Keryhe.Telemetry.TestDataGenerator \
  set "Generator:Tenants:1:ApiKey" "<key for contoso-dev>"
dotnet user-secrets --project src/Keryhe.Telemetry.TestDataGenerator \
  set "Generator:Tenants:2:ApiKey" "<key for globex-payments>"

dotnet run --project src/Keryhe.Telemetry.TestDataGenerator
```

It emits live telemetry through the real OpenTelemetry SDK to `http://localhost:5117`. To backfill history first
(24 hours by default; running a backfill twice over the same window stores it twice):

```bash
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator -- --Generator:Backfill:Enabled=true
```

See [CONFIGURATION.md](CONFIGURATION.md#test-data-generator-generator) for every `Generator` setting.

## Deploying

The collector and the API are separate hosts, on a single node or many: `Keryhe.Telemetry.Collector.Server`
(gRPC OTLP ingestion) and `Keryhe.Telemetry.Api.Server` (REST API, the compiled Angular UI, alerting and
retention). They share only the database, and each reads its own connection string
(`ConnectionStrings:Collector`, `ConnectionStrings:Api`), so they can point at different endpoints.

```bash
dotnet publish src/Keryhe.Telemetry.Collector.Server -c Release -o ./publish-collector
dotnet publish src/Keryhe.Telemetry.Api.Server -c Release -o ./publish-api
```

The collector listens on `https://0.0.0.0:7057` (HTTP/2) and needs a TLS certificate configured on that endpoint
(or TLS terminated by a proxy in front, with `Telemetry:Collector:AllowInsecureTransport=true`): outside Development it
refuses to start on a plaintext address, because API keys travel in every export. See the
[collector README](../src/Keryhe.Telemetry.Collector/README.md) for the certificate block, exporter settings and key
lifecycle. Do not set `ASPNETCORE_URLS` on the collector: it overrides the per-endpoint HTTP/2 settings. On Windows,
`install-service.bat [collector-publish-folder] [api-publish-folder]` installs both as services
(`KeryheTelemetryCollector`, `KeryheTelemetryApi`; default folders `publish-collector` and `publish-api`).

`Keryhe.Telemetry.Api.Server` serves the compiled Angular UI alongside the REST API, so it needs no
separate web server for the SPA and no CORS configuration.

The Angular UI is packaged separately, as `Keryhe.Telemetry.Ui` (a NuGet package like the other
class libraries; see the [README](../README.md#build-your-own-host) if you're composing your own
host rather than running this one as-is). `Keryhe.Telemetry.Api.Server` references it via a plain
`ProjectReference`, so publishing it also builds `src/telemetry-client` (`npm ci && npm run build`)
and stages the output into `Keryhe.Telemetry.Ui`'s own `wwwroot`, from where it flows into the
published host as static web assets. Deep links such as a trace page are handled by an SPA fallback to
`index.html`.

- Plain `dotnet build` never invokes npm: `Keryhe.Telemetry.Ui`'s build is incremental (a stamp
  file skips the npm build once it's already current), so this stays true even across a full
  solution build. A missing Node toolchain only warns and packages an empty UI.
- Pass `-p:BuildSpa=false` to publish against an already-built `src/telemetry-client/dist`
  (useful when CI builds the UI in a separate stage). The flag lives on `Keryhe.Telemetry.Ui`, not
  the host, but propagates transitively through the `ProjectReference`.
- The API's location is **runtime** configuration, not baked into the compiled bundle: the client
  fetches `config.json` before it bootstraps and reads `apiUrl` from it (served by
  `UseKeryheTelemetryUi()`; with `TelemetryUi:ApiBasePath` unset it follows `Telemetry:Api:BasePath`).
  This is what lets the same published bundle work for a host that mounts the API
  somewhere else; see the README's `ApiBasePath` example.
- **Where the UI itself is mounted is runtime configuration too.** Set `TelemetryUi:BasePath` to
  serve it under a prefix instead of the origin root:

  ```bash
  dotnet run --project src/Keryhe.Telemetry.Api.Server -- --TelemetryUi:BasePath=/ui
  ```

  The bundle's `<base href>` is rewritten to match at startup, so its assets, its `config.json`
  fetch and its client-side router all re-root together with no rebuild. Two constraints follow
  from the value being baked into the served HTML rather than derived per request: behind a reverse
  proxy the prefix must be **forwarded** (`proxy_pass http://app;`) rather than stripped
  (`proxy_pass http://app/;`), and once it is set the origin root returns 404, which is the point,
  since it is what lets another app own `/`. `BasePath` moves the UI only; set
  `TelemetryUi:ApiBasePath` and `Telemetry:Api:BasePath` as well if your deployment relocates the API too.

**API authorization is off by default.** The in-repo `Api.Server` is a development example with no sign-in.
To protect the API in a deployment, register an authentication scheme and policies in your own host and
enable `Telemetry:Api:Authorization` (operation policies, claim-to-tenant mappings); the API returns 401
when unauthenticated and 403 for a refused tenant or operation. See
[CONFIGURATION.md](CONFIGURATION.md#routes-and-authorization-telemetryapi) and the
[API package README](../src/Keryhe.Telemetry.Api/README.md).

## Database Schema

### Key Tables

- `resources` — Entities producing telemetry (services, hosts, etc.)
- `instrumentation_scopes` — Library/instrumentation information
- `spans` — Trace span data; a span's events and links are `events_json`/`links_json` columns on its row
- `metrics` — Base metric metadata (one catalog row per resource, name, type and scope)
- `metric_last_seen` — Newest data point per metric, so the metrics catalog can tell which metrics have data in a range
- `gauge_data_points`, `sum_data_points`, `histogram_data_points`, `exponential_histogram_data_points`, `summary_data_points` — Type-specific metric data, each carrying its own exemplars (with trace correlation) in an `exemplars_json` column
- `log_records` — Log entries with severity and trace correlation
- `tenants` — Tenant registry (none is seeded; see step 3)
- `api_keys` — Hashed API keys scoped to a tenant, with an optional `expires_at`, used for ingestion auth
- `alert_rules` — Alert rule definitions (type, condition JSON, webhook URL, cooldown)
- `alert_events` — Audit log of all fired alert events
- `retention_settings` — The single row holding the retention windows (traces 90 days, logs 90, metrics 180 by default)
- `schema_version` — Applied schema versions
- `request_rollup_minute`, `log_rollup_minute` — Per-minute rollups of inbound spans and log records that the dashboard, trace list and logs page read for their cards and charts (rows are partial and summed on read; forward-only, so ranges before an upgrade show empty charts)
- MySQL only: `request_rollup_hour`, `log_rollup_hour` and `rollup_compaction` — the hour tier of those rollups
- ClickHouse only: `trace_index` (per-trace time bounds used to locate a trace) and the materialized views feeding it, `metric_last_seen` and the two rollup tables

Spans, log records and data points are plain appends with no unique key and no foreign keys, so a re-delivered
batch is stored again and reads tolerate it. The trace and log lists are computed from the raw rows; the cards and
charts above them come from the rollups, which count **requests** (inbound server and consumer spans), not traces.

When using the TimescaleDB provider, `spans`, `log_records` and the five data-point tables are hypertables
(6 hour chunks for spans and logs, 12-24 hours for data points). Compression activates at 7 days, and retention
drops whole chunks (`drop_chunks`), so a row survives until its entire chunk is older than the cutoff. There are no
continuous aggregates.

When using the MySQL provider (MySQL 8.0.19+), the schema mirrors the SQL Server relational layout with MySQL-native types (`AUTO_INCREMENT` surrogate keys, `JSON` columns for attributes, `DATETIME(6)` timestamps). Hash-based deduplication of resources/scopes uses `INSERT ... ON DUPLICATE KEY UPDATE`, and writes are batched as multi-row inserts.

When using the ClickHouse provider, tables use the `MergeTree` family with day partitioning. Because ClickHouse has no auto-increment or `RETURNING`, surrogate `id` values are generated by the application. Resources, scopes, metrics and the control-plane tables are `ReplacingMergeTree`, so their dedup is *eventual* (a merge, or `OPTIMIZE ... FINAL`, resolves duplicates); spans and logs are plain appends. Retention drops fully expired day partitions. Control-plane operations are best-effort: alert-rule edits use `ALTER TABLE ... UPDATE` mutations and the cooldown fire-claim is not atomic. See `../CLAUDE.md` for the full list of ClickHouse-specific behaviors.

Free-text and `key:value` search is unindexed on every provider, so a search or "slow" request is limited to
`Telemetry:Query:RawSearchWindowHours` (default 24); exports are limited to a provider-dependent window
(7 days, or 1 day on SQL Server and MySQL). `GET <api base>/capabilities` reports the active limits.

## Alerting

Alert rules are managed through the **Alerts** page in the UI. Each rule specifies:

- **Type**: `MetricThreshold`, `ErrorRate`, `SlowTrace`, or `LogSeveritySpike`
- **Service** (optional): scopes the rule to a single service
- **Condition**: JSON-encoded parameters specific to the rule type
- **Webhook URL**: receives an HTTP POST payload when the rule fires
- **Cooldown**: minimum minutes between repeat firings of the same rule

Rules are evaluated by a background worker (`AlertEvaluationWorker`) hosted in
`Keryhe.Telemetry.Api.Server`. It runs every `Telemetry:AlertEvaluation:IntervalSeconds` (default `60`),
iterating all tenants with enabled rules and dispatching each to its evaluator. An atomic
fire-claim guards the cooldown so a rule fires once even across multiple instances. Configure
it in `src/Keryhe.Telemetry.Api.Server/appsettings.json`:

```json
{
  "Telemetry": {
    "AlertEvaluation": {
      "IntervalSeconds": 60,
      "Enabled": true
    }
  }
}
```

Set `Enabled` to `false` to keep the alert API and rule storage available while disabling
automatic evaluation.

## Retention

`RetentionWorker` in `Keryhe.Telemetry.Api.Server` sweeps old telemetry every `Telemetry:Retention:IntervalSeconds`
(default 3600; `Enabled=false` stops it). How many days to keep is not configuration: it is the
`retention_settings` row, edited on the **Settings** page in the UI. The mechanism depends on the provider:
bounded-batch deletes per tenant (PostgreSQL, SQL Server, MySQL), `drop_chunks` (Timescale) or dropping whole day
partitions (ClickHouse). Each sweep logs "Retention sweep complete" with the rows removed and how long it took.

## Testing

```bash
# Every provider against real Testcontainers databases (requires Docker)
dotnet test tests/Keryhe.Telemetry.IntegrationTests
# One provider: PostgreSQL | Timescale | SqlServer | MySql | ClickHouse
dotnet test tests/Keryhe.Telemetry.IntegrationTests --filter Provider=SqlServer

# Test data generator simulation tests (no database or Docker)
dotnet test tests/Keryhe.Telemetry.TestDataGenerator.Tests
```

`tests/Keryhe.Telemetry.StressTests` is a manual, Docker-based load harness (never part of `dotnet test`); its
[README](../tests/Keryhe.Telemetry.StressTests/README.md) covers prerequisites, profiles and reports.
