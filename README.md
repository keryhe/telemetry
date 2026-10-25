# Keryhe Telemetry

> [!WARNING]
> **Experimental: not for production use.** This is a personal project and a proof of concept, built to explore
> ideas around OpenTelemetry ingestion, storage and visualization. It has not been hardened, security-reviewed or
> tested at production scale, and its schema and APIs may change without notice or a migration path. Use it to
> learn, experiment or borrow ideas, but do not rely on it for real workloads or sensitive data.

A self-hosted OpenTelemetry (OTLP) platform for traces, metrics and logs: a collector that receives OTLP from any
OpenTelemetry SDK, a REST API to query it, and an Angular UI. Telemetry is stored in PostgreSQL, SQL Server, MySQL,
ClickHouse or Parquet files; tenants, API keys and alert rules (the control plane) in PostgreSQL, SQL Server, MySQL or a SQLite file.

## Features

- Traces, metrics and logs over OTLP gRPC and HTTP, linked by trace and span ids
- Dashboard, trace, metric and log explorers, service map and latency heatmaps
- Multi-tenant: a per-tenant API key on every export, tenant-scoped API with pluggable authorization
- Alerts (metric threshold, error rate, slow requests, log severity spikes) with webhooks
- Export to NDJSON or CSV, per-tenant retention
- `Keryhe.Telemetry.Admin`, a console tool for tenants and API keys

## Quick Start (PostgreSQL)

```bash
# 1. Create the database and apply the two schema scripts (control plane first, then telemetry)
createdb telemetry
psql -d telemetry -f schema/PostgreSQL-ControlPlane.sql
psql -d telemetry -f schema/PostgreSQL-Telemetry.sql

# 2. Point both hosts at it via User Secrets (keeps credentials out of source control).
#    ControlPlane:Provider and Database:Provider are PostgreSQL in the shipped appsettings.json.
dotnet user-secrets --project src/Keryhe.Telemetry.Api.Server \
  set "ConnectionStrings:Api"          "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
dotnet user-secrets --project src/Keryhe.Telemetry.Api.Server \
  set "ConnectionStrings:ControlPlane" "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
dotnet user-secrets --project src/Keryhe.Telemetry.Collector.Server \
  set "ConnectionStrings:Collector"    "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
dotnet user-secrets --project src/Keryhe.Telemetry.Collector.Server \
  set "ConnectionStrings:ControlPlane" "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"

# 3. Build and run the two hosts (each in its own terminal): gRPC ingestion, then the REST API + UI
dotnet build Telemetry.sln
dotnet run --project src/Keryhe.Telemetry.Collector.Server
dotnet run --project src/Keryhe.Telemetry.Api.Server

# 4. Create a tenant and an API key (console tool; its connection string goes in its own user secrets)
dotnet user-secrets --project src/Keryhe.Telemetry.Admin \
  set "ConnectionStrings:Admin" "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
dotnet run --project src/Keryhe.Telemetry.Admin

# 5. Run the Angular dev server
cd src/telemetry-client && npm install && npm start
```

Open `http://localhost:4201` (the Angular dev server), or `http://localhost:5188/telemetry`, where the API host serves the built UI. Ingestion requires an `Authorization: Bearer <key>` header on
every OTLP request — see [Configure an API key](docs/SETUP.md#3-create-a-tenant-and-an-api-key)
in the full setup guide. In Development the collector also listens on plaintext `http://localhost:5117`;
outside Development it requires TLS (`https://…:7057`), because keys travel in every export.

For the other providers, deployment and the test data generator see **[docs/SETUP.md](docs/SETUP.md)**; every setting is in
**[docs/CONFIGURATION.md](docs/CONFIGURATION.md)**.

## Projects

| Project | Role |
|---|---|
| `Keryhe.Telemetry.Collector.Server` / `.Api.Server` | The two hosts: OTLP ingestion, and the REST API that also serves the UI |
| `Keryhe.Telemetry.Collector`, `.Api`, `.Ui` | The libraries behind them, for [your own hosts](docs/ARCHITECTURE.md#build-your-own-host) |
| `Keryhe.Telemetry.PostgreSQL`, `.SqlServer`, `.MySql`, `.ClickHouse`, `.Parquet`, `.Sqlite` | Storage providers |
| `Keryhe.Telemetry.Admin` | Console tool for tenants and API keys |
| `Keryhe.Telemetry.TestDataGenerator` | Emits simulated multi-tenant telemetry |
| `src/telemetry-client` | The Angular source of the UI |
| `tests/` | Integration tests, the stress harness and the generator's tests ([Testing](docs/SETUP.md#testing)) |

## Documentation

- [docs/SETUP.md](docs/SETUP.md): setup per provider, deployment, alerting, retention, testing
- [docs/CONFIGURATION.md](docs/CONFIGURATION.md): every configuration key and its default
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): how it fits together, building your own host
- [Collector](docs/COLLECTOR.md), [API](docs/API.md), [UI](docs/UI.md), [Parquet](docs/PARQUET.md), [ClickHouse](docs/CLICKHOUSE.md),
  [SQLite](docs/SQLITE.md), [provider packages](docs/PROVIDERS.md), [stress tests](docs/STRESS-TESTS.md)

## License

[MIT](LICENSE)
