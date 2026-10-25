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

## Quick Start (Parquet + SQLite)

The shipped `appsettings.json` of both hosts stores telemetry as Parquet files and the control plane (tenants, API keys,
alert rules) in a SQLite file, so the quick start needs **no database server**: one data directory and one file on one
machine. Both paths must be absolute and the same for every host and tool.

```bash
# 1. Pick the locations. Whichever host starts first creates the Parquet root and the SQLite file, so there is no schema step
TELEMETRY_HOME="$HOME/keryhe-telemetry"

# 2. Point both hosts at them via User Secrets (Database:Provider=Parquet and ControlPlane:Provider=Sqlite are already set)
dotnet user-secrets --project src/Keryhe.Telemetry.Api.Server \
  set "Telemetry:Parquet:RootPath"     "$TELEMETRY_HOME/data"
dotnet user-secrets --project src/Keryhe.Telemetry.Api.Server \
  set "ConnectionStrings:ControlPlane" "Data Source=$TELEMETRY_HOME/control-plane.db"
dotnet user-secrets --project src/Keryhe.Telemetry.Collector.Server \
  set "Telemetry:Parquet:RootPath"     "$TELEMETRY_HOME/data"
dotnet user-secrets --project src/Keryhe.Telemetry.Collector.Server \
  set "ConnectionStrings:ControlPlane" "Data Source=$TELEMETRY_HOME/control-plane.db"

# 3. Build and run the two hosts (each in its own terminal): gRPC ingestion, then the REST API + UI
dotnet build Telemetry.sln
dotnet run --project src/Keryhe.Telemetry.Collector.Server
dotnet run --project src/Keryhe.Telemetry.Api.Server

# 4. Create a tenant and an API key (console tool; it only checks the SQLite file, so run it after a host has started)
dotnet user-secrets --project src/Keryhe.Telemetry.Admin \
  set "ControlPlane:Provider"   "Sqlite"
dotnet user-secrets --project src/Keryhe.Telemetry.Admin \
  set "ConnectionStrings:Admin" "Data Source=$TELEMETRY_HOME/control-plane.db"
dotnet run --project src/Keryhe.Telemetry.Admin

# 5. Run the Angular dev server
cd src/telemetry-client && npm install && npm start
```

The collector buffers records for a few seconds (`Telemetry:Parquet:LingerMilliseconds`, 5 s) before writing a Parquet
file, so new data shows up in the UI about that long after it arrives. Keep the directory on a local disk (not a network share): the collector, the API host and
the Admin tool must all open the same files. See [Parquet](docs/PARQUET.md) and [SQLite](docs/SQLITE.md).

Open `http://localhost:4201` (the Angular dev server), or `http://localhost:5188/telemetry`, where the API host serves the built UI. Ingestion requires an `Authorization: Bearer <key>` header on
every OTLP request — see [Configure an API key](docs/SETUP.md#3-create-a-tenant-and-an-api-key)
in the full setup guide. In Development the collector also listens on plaintext `http://localhost:5117`;
outside Development it requires TLS (`https://…:7057`), because keys travel in every export.

For PostgreSQL, SQL Server, MySQL and ClickHouse, deployment and the test data generator see **[docs/SETUP.md](docs/SETUP.md)**; every setting is in
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
