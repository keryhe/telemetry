# Keryhe Telemetry

A self-hosted OpenTelemetry (OTLP) ingestion and visualization platform for traces, metrics, and logs. Provides a gRPC server for receiving telemetry from any OpenTelemetry SDK, a REST API for querying the data, and an Angular UI for visualization.

## Features

- **Complete OTLP Support**: Handles traces, metrics, and logs as defined in opentelemetry-proto
- **Angular UI**: Web interface (Angular 20) for exploring traces, metrics, logs, dashboards, and alerts
- **REST API**: ASP.NET Core Web API (`Keryhe.Telemetry.Api`) with Swagger support
- **Multiple Database Providers**: Choose between plain PostgreSQL, PostgreSQL + TimescaleDB, SQL Server, MySQL, or ClickHouse (columnar/OLAP)
- **Trace Correlation**: Links logs and metrics to traces via trace and span IDs
- **Built-in Analytics**: Pre-configured views for service maps, trace summaries, and log analysis
- **Alerting**: Rule-based alerts (metric threshold, error rate, slow traces, log severity spikes) with configurable cooldowns and webhook delivery

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
read/write implementation and is selected by the host at startup.

| Project | Role |
|---------|------|
| `Keryhe.Telemetry.Core` | Domain interfaces and models shared across projects |
| `Keryhe.Telemetry.Data` | Provider-agnostic write repositories, ingestion channel + background worker, Dapper read-repository bases |
| `Keryhe.Telemetry.PostgreSQL` / `.Timescale` / `.SqlServer` / `.MySql` / `.ClickHouse` | Per-provider read/write implementations |
| `Keryhe.Telemetry.Collector` / `.Collector.Server` | gRPC OTLP ingestion (class library + thin host) |
| `Keryhe.Telemetry.Api` / `.Api.Server` | REST API controllers and tenant middleware (class library + thin host) |
| `Keryhe.Telemetry.Server` | All-in-one host: gRPC ingestion, REST API, and the Angular UI in a single process |
| `Keryhe.Telemetry.Alerting` | Alert rule evaluators, webhook delivery, periodic evaluation worker |
| `Keryhe.Telemetry.TestDataGenerator` | Worker service that emits synthetic telemetry |
| `src/telemetry-client` | Angular 20 SPA (Dashboard, Traces, Metrics, Logs, Alerts) |

See [CLAUDE.md](CLAUDE.md) for a deeper architectural walkthrough (composition roots, ingestion
pipeline, multi-tenancy, provider-specific caveats).

## Quick Start (PostgreSQL)

```bash
# 1. Create the database and apply the schema
createdb telemetry
psql -d telemetry -f schema/PostgreSQL-Schema.sql

# 2. Point both hosts at it via User Secrets (keeps credentials out of source control)
dotnet user-secrets --project src/Keryhe.Telemetry.Api.Server \
  set "ConnectionStrings:Read"  "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
dotnet user-secrets --project src/Keryhe.Telemetry.Collector.Server \
  set "ConnectionStrings:Write" "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"

# 3. Build and run the all-in-one host
dotnet build Telemetry.sln
dotnet run --project src/Keryhe.Telemetry.Server

# 4. Run the Angular dev server
cd src/telemetry-client && npm install && npm start
```

Open `http://localhost:4201`. Ingestion requires an `Authorization: Bearer <key>` header on
every OTLP request — see [Configure an API key](docs/SETUP.md#3-configure-an-api-key-for-the-authorization-header)
in the full setup guide.

For other database providers (TimescaleDB, SQL Server, MySQL, ClickHouse), Docker recipes,
running the split hosts, deploying to production, the full schema reference, and alerting
configuration, see **[docs/SETUP.md](docs/SETUP.md)**.

## License

[MIT](LICENSE)

## Acknowledgments

Built according to the [OpenTelemetry Protocol Specification](https://github.com/open-telemetry/opentelemetry-proto)
