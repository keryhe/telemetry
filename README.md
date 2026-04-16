# Keryhe Telemetry

A self-hosted OpenTelemetry (OTLP) ingestion and visualization platform for traces, metrics, and logs. Provides a gRPC server for receiving telemetry from any OpenTelemetry SDK and a Blazor UI for querying and visualizing the data.

## Overview

This solution receives OpenTelemetry Protocol (OTLP) data via gRPC, stores it in a PostgreSQL database with a normalized, optimized schema, and exposes it through a Blazor Server web application.

## Features

- **Complete OTLP Support**: Handles traces, metrics, and logs as defined in opentelemetry-proto
- **Blazor UI**: Web interface for exploring traces, metrics, logs, dashboards, and service metrics
- **Normalized Schema**: Efficient storage with proper relationships and hash-based deduplication
- **All Metric Types**: Gauge, Sum, Histogram, Exponential Histogram, and Summary
- **Trace Correlation**: Links logs and metrics to traces via trace and span IDs
- **Built-in Analytics**: Pre-configured views for service maps, trace summaries, and log analysis

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
  → PostgreSQL
  → Keryhe.Telemetry.Client (Blazor UI)
```

| Project | Role |
|---------|------|
| `Keryhe.Telemetry.Core` | Domain interfaces and models |
| `Keryhe.Telemetry.Data` | EF Core DbContexts and repository implementations |
| `Keryhe.Telemetry.Server` | gRPC server receiving OTLP telemetry |
| `Keryhe.Telemetry.Client` | Blazor Server UI for visualization |
| `Keryhe.Telemetry.TestDataGenerator` | Worker service that emits synthetic telemetry |

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)
- PostgreSQL 14 or higher

## Setup

**1. Create the database and apply the schema:**

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

### Built-in Views

- `trace_summary` — Aggregated trace information
- `service_map` — Service-to-service relationships
- `service_map_detailed` — Service map with performance metrics
- `log_severity_stats` — Log severity distribution over time

## License

[MIT]

## Acknowledgments

Built according to the [OpenTelemetry Protocol Specification](https://github.com/open-telemetry/opentelemetry-proto)
