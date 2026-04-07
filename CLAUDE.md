# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build Telemetry.sln

# Run the gRPC ingestion server
dotnet run --project src/Keryhe.Telemetry.Server

# Run the Blazor UI
dotnet run --project src/Keryhe.Telemetry.Client

# Run test data generator (sends synthetic OTLP data to the server)
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator

# Apply database schema
psql -d telemetry -f schema/PostgreSQL-Schema.sql
```

There are no test projects in the solution.

## Architecture

This is an **OpenTelemetry (OTLP) ingestion and visualization platform** — a self-hosted alternative to tools like Jaeger or Grafana Tempo. It receives telemetry via gRPC, stores it in PostgreSQL, and visualizes it through a Blazor UI.

### Projects

| Project | Role |
|---------|------|
| `Keryhe.Telemetry.Core` | Domain interfaces and models shared across projects |
| `Keryhe.Telemetry.Data` | EF Core DbContexts and repository implementations |
| `Keryhe.Telemetry.Server` | gRPC server receiving OTLP traces, metrics, and logs |
| `Keryhe.Telemetry.Client` | Blazor Server UI for visualization (MudBlazor + ApexCharts) |
| `Keryhe.Telemetry.TestDataGenerator` | Worker service that emits synthetic telemetry via OpenTelemetry SDK |

### Data Flow

```
OpenTelemetry SDKs (any language)
  → OTLP gRPC (port 5117) → Keryhe.Telemetry.Server
  → TelemetryWriteDbContext → PostgreSQL
  → TelemetryReadDbContext → Keryhe.Telemetry.Client (Blazor UI)
```

**CQRS split**: The Server uses `TelemetryWriteDbContext` (full change tracking); the Client uses `TelemetryReadDbContext` (no-tracking, read-only). Both contexts share the same `TelemetryModelConfiguration` for entity mappings.

### Key Patterns

**Repository pattern with read/write split:**
- Write repos (`TraceWriteRepository`, `MetricWriteRepository`, `LogWriteRepository`) — used by Server
- Read repos (`TraceReadRepository`, `MetricReadRepository`, `LogReadRepository`) — used by Client
- Core interfaces defined in `Keryhe.Telemetry.Core` (e.g. `ITraceWriteRepository`, `ITraceReadRepository`)

**Server gRPC services** (`src/Keryhe.Telemetry.Server/Services/`): Inherit from protobuf-generated base classes, convert OTLP protobuf messages to Core domain models, delegate to write repositories, return partial success responses.

**Client service layer** (`src/Keryhe.Telemetry.Client/Services/`): Interface-based services (`ITraceService`, `IMetricService`, `ILogService`) wrapping read repositories with query/aggregation logic.

**Page state classes** (`src/Keryhe.Telemetry.Client/Services/State/`): Scoped services holding per-page state (filters, selected time range, etc.) shared across Blazor components.

**Hash-based deduplication**: Resources and InstrumentationScopes are deduplicated via hash columns (`ResourceHash`, `ScopeHash`) with UNIQUE constraints — inserts use ON CONFLICT DO NOTHING or DO UPDATE.

**JSONB for attributes**: OpenTelemetry key-value attributes are stored as JSONB columns (`Attributes`, `FilteredAttributes`) rather than normalized tables.

### Database

PostgreSQL with 13 main tables: `resources`, `instrumentation_scopes`, `spans`, `span_events`, `span_links`, `metrics`, `gauge_data_points`, `sum_data_points`, `histogram_data_points`, `exponential_histogram_data_points`, `summary_data_points`, `exemplars`, `log_records`.

Schema also includes built-in views: `trace_summary`, `service_map`, `service_map_detailed`, `log_severity_stats`.

Connection strings:
- Server reads from `ConnectionStrings:Write` in `src/Keryhe.Telemetry.Server/appsettings.json`
- Client reads from `ConnectionStrings:Read` in `src/Keryhe.Telemetry.Client/appsettings.json`

Both point to: `Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>`

### Proto Files

OpenTelemetry proto files live in `src/Keryhe.Telemetry.Server/` and are compiled to C# gRPC stubs automatically via MSBuild (`Grpc.Tools`). Covers traces, metrics, logs, profiles, resources, and common types.
