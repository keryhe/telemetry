# Keryhe Telemetry

A self-hosted OpenTelemetry (OTLP) ingestion and visualization platform for traces, metrics, and logs. Provides a gRPC server for receiving telemetry from any OpenTelemetry SDK and a Blazor UI for querying and visualizing the data.

## Overview

This solution receives OpenTelemetry Protocol (OTLP) data via gRPC, stores it in a PostgreSQL database with TimescaleDB for time-series storage, and exposes it through a Blazor Server web application.

## Features

- **Complete OTLP Support**: Handles traces, metrics, and logs as defined in opentelemetry-proto
- **Blazor UI**: Web interface for exploring traces, metrics, logs, dashboards, and service metrics
- **Normalized Schema**: Efficient storage with proper relationships and hash-based deduplication
- **All Metric Types**: Gauge, Sum, Histogram, Exponential Histogram, and Summary
- **Trace Correlation**: Links logs and metrics to traces via trace and span IDs
- **Built-in Analytics**: Pre-configured views for service maps, trace summaries, and log analysis
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
  → OTLP gRPC (port 5117) → Keryhe.Telemetry.Server
  → PostgreSQL + TimescaleDB
  → Keryhe.Telemetry.Client (Blazor UI)
    → background alert evaluation → webhook notifications
```

| Project | Role |
|---------|------|
| `Keryhe.Telemetry.Core` | Domain interfaces and models |
| `Keryhe.Telemetry.Data` | EF Core DbContexts and repository implementations |
| `Keryhe.Telemetry.Server` | gRPC server receiving OTLP telemetry |
| `Keryhe.Telemetry.Client` | Blazor Server UI for visualization |
| `Keryhe.Telemetry.Alerting` | Alert rule evaluators and webhook notification delivery |
| `Keryhe.Telemetry.TestDataGenerator` | Worker service that emits synthetic telemetry |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- PostgreSQL 14 or higher
- TimescaleDB extension (required — metrics and log tables are hypertables; install before applying the schema)

## Setup

**1. Create the database and apply the schema:**

Ensure the TimescaleDB extension is installed and enabled in your PostgreSQL instance (`CREATE EXTENSION IF NOT EXISTS timescaledb;`). The schema script creates hypertables and will fail if TimescaleDB is unavailable.

```bash
createdb telemetry
psql -d telemetry -f schema/PostgreSQL-Schema.sql
```

**2. Configure connection strings:**

Update `src/Keryhe.Telemetry.Server/appsettings.json`:
```json
{
  "ConnectionStrings": {
    "Write": "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
  }
}
```

Update `src/Keryhe.Telemetry.Client/appsettings.json`:
```json
{
  "ConnectionStrings": {
    "Read": "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
  }
}
```

**3. Build:**

```bash
dotnet build Telemetry.sln
```

**4. Run the server and client (in separate terminals):**

```bash
dotnet run --project src/Keryhe.Telemetry.Server
dotnet run --project src/Keryhe.Telemetry.Client
```

**5. (Optional) Generate test data:**

```bash
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator
```

The test data generator sends synthetic traces, metrics, and logs to the server at `http://localhost:5117` on a configurable interval.

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
- `gauge_data_points`, `sum_data_points`, `histogram_data_points`, `exponential_histogram_data_points`, `summary_data_points` — Type-specific metric data (TimescaleDB hypertables)
- `exemplars` — Metric exemplars with trace correlation
- `log_records` — Log entries with severity and trace correlation (TimescaleDB hypertable)
- `tenants` — Tenant registry (a `default` tenant is seeded on first run)
- `api_keys` — Hashed API keys scoped to a tenant, used for ingestion auth
- `alert_rules` — Alert rule definitions (type, condition JSON, webhook URL, cooldown)
- `alert_events` — Audit log of all fired alert events

### Built-in Views

- `trace_summary` — Aggregated trace information
- `service_map` — Service-to-service relationships
- `service_map_detailed` — Service map with performance metrics
- `log_severity_stats` — Log severity distribution (compatibility alias)
- `log_severity_stats_daily` — TimescaleDB continuous aggregate; daily severity counts, refreshes every 5 minutes

## Alerting

Alert rules are managed through the **Alerts** page in the UI. Each rule specifies:

- **Type**: `MetricThreshold`, `ErrorRate`, `SlowTrace`, or `LogSeveritySpike`
- **Service** (optional): scopes the rule to a single service
- **Condition**: JSON-encoded parameters specific to the rule type
- **Webhook URL**: receives an HTTP POST payload when the rule fires
- **Cooldown**: minimum minutes between repeat firings of the same rule

The evaluation loop runs every 60 seconds by default. Configure via `AlertEvaluation:IntervalSeconds` in `src/Keryhe.Telemetry.Client/appsettings.json`.

## License

[MIT]

## Acknowledgments

Built according to the [OpenTelemetry Protocol Specification](https://github.com/open-telemetry/opentelemetry-proto)
