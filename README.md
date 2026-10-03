# Keryhe Telemetry

A self-hosted OpenTelemetry (OTLP) ingestion and visualization platform for traces, metrics, and logs. Provides a gRPC server for receiving telemetry from any OpenTelemetry SDK, a REST API for querying the data, and an Angular UI for visualization.

## Features

- **Complete OTLP Support**: Handles traces, metrics, and logs as defined in opentelemetry-proto
- **Angular UI**: Web interface (Angular 20) for exploring traces, metrics, logs, dashboards, and alerts
- **REST API**: ASP.NET Core Web API (`Keryhe.Telemetry.Api`) with Swagger support
- **Multiple Database Providers**: Choose between plain PostgreSQL, PostgreSQL + TimescaleDB, SQL Server, MySQL, or ClickHouse (columnar/OLAP)
- **Trace Correlation**: Links logs and metrics to traces via trace and span IDs
- **Built-in Analytics**: Service maps, trace summaries and latency heatmaps, and log severity analysis, computed on demand from the raw data
- **Multi-tenant**: Every OTLP export carries a per-tenant API key (hashed in the database, optional expiry); the REST API is scoped by tenant and supports pluggable authorization
- **Export and retention**: Stream logs, traces and metrics as NDJSON or CSV; retention windows are editable in the UI and enforced by a background sweep
- **Admin tool**: A console app (`Keryhe.Telemetry.Admin`) for creating tenants and API keys
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
| `Keryhe.Telemetry.Core` | Domain interfaces and models, plus the provider-agnostic write repositories, ingestion channel + background worker and Dapper read-repository bases |
| `Keryhe.Telemetry.PostgreSQL` / `.Timescale` / `.SqlServer` / `.MySql` / `.ClickHouse` | Per-provider read/write implementations |
| `Keryhe.Telemetry.Collector` / `.Collector.Server` | gRPC OTLP ingestion with per-tenant API key authentication (class library + thin host) |
| `Keryhe.Telemetry.Api` / `.Api.Server` | REST API controllers, base-path routing and authorization, alert evaluation (rules, webhooks, periodic worker) and retention sweeps (class library + thin host) |
| `Keryhe.Telemetry.Ui` | Prebuilt Angular UI, packaged as static web assets — see [Build your own host](#build-your-own-host) below |
| `Keryhe.Telemetry.Admin` | Console tool for tenants and API keys (PostgreSQL, Timescale, SQL Server) |
| `Keryhe.Telemetry.TestDataGenerator` | Worker service that simulates a multi-tenant e-commerce system and emits realistic traces, logs and metrics (24h backfill, then live) |
| `src/telemetry-client` | Angular 20 SPA source (Dashboard, Traces, Metrics, Logs, Alerts, Settings) — built into `Keryhe.Telemetry.Ui`, not part of the .sln |
| `tests/` | Docker-based per-provider integration tests, a stress-test harness and the test data generator's tests — see [Testing](#testing) |

See [CLAUDE.md](CLAUDE.md) for a deeper architectural walkthrough (composition roots, ingestion
pipeline, multi-tenancy, provider-specific caveats).

## Quick Start (PostgreSQL)

```bash
# 1. Create the database and apply the schema
createdb telemetry
psql -d telemetry -f schema/PostgreSQL-Schema.sql

# 2. Point both hosts at it via User Secrets (keeps credentials out of source control)
dotnet user-secrets --project src/Keryhe.Telemetry.Api.Server \
  set "ConnectionStrings:Api"       "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"
dotnet user-secrets --project src/Keryhe.Telemetry.Collector.Server \
  set "ConnectionStrings:Collector" "Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>"

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

Open `http://localhost:4201`. Ingestion requires an `Authorization: Bearer <key>` header on
every OTLP request — see [Configure an API key](docs/SETUP.md#3-create-a-tenant-and-an-api-key)
in the full setup guide. In Development the collector also listens on plaintext `http://localhost:5117`;
outside Development it requires TLS (`https://…:7057`), because keys travel in every export.

For other database providers (TimescaleDB, SQL Server, MySQL, ClickHouse), Docker recipes,
running the two hosts, deploying to production, the full schema reference, and alerting
configuration, see **[docs/SETUP.md](docs/SETUP.md)**. Every configuration key and its default is
listed in **[docs/CONFIGURATION.md](docs/CONFIGURATION.md)**.

## Build your own host

Every piece of this stack ships as a NuGet package — `Keryhe.Telemetry.Core`, `.Api`,
`.Collector`, `.Ui`, and the five database providers — so you can compose your own ASP.NET Core
host instead of running `Keryhe.Telemetry.Api.Server`/`.Server` as-is: add your own middleware,
combine it with an existing application, or change what gets exposed.

```xml
<ItemGroup>
  <PackageReference Include="Keryhe.Telemetry.Api" Version="1.3.0" />
  <PackageReference Include="Keryhe.Telemetry.Ui" Version="1.3.0" />
  <!-- Plus exactly one provider package, matching Database:Provider below: -->
  <PackageReference Include="Keryhe.Telemetry.Timescale" Version="1.3.0" />
</ItemGroup>
```

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddKeryheTelemetryApi(builder.Configuration); // controllers, tenant context, authorization
builder.Services.AddKeryheTelemetryUi(builder.Configuration);  // binds the TelemetryUi section
builder.Services.AddTimescaleApiServices(builder.Configuration); // reads ConnectionStrings:Api

var app = builder.Build();

// Paired with an explicit UseRouting() call right after it — see CLAUDE.md's "UI hosting" section
// for why that second part matters.
app.UseKeryheTelemetryUi();
app.UseRouting();

// Register your own authentication scheme and policies when you enable
// Telemetry:Api:Authorization; with it off these are no-ops.
app.UseAuthentication();
app.UseAuthorization();

app.MapKeryheTelemetryApi();   // replaces MapControllers(); also gives unknown API paths a JSON 404
app.MapKeryheTelemetryUiFallback();

app.Run();
```

`Keryhe.Telemetry.Ui` ships the compiled Angular bundle prebuilt — no Node, no npm, nothing to
build — and serves it at `/`, same-origin with the API at `/api` by default. If your host mounts
the UI or the API somewhere else, or wants its own product name in the header bar and browser tab
instead of "Sentinel", configure it rather than rebuilding it:

```jsonc
{
  "TelemetryUi": {
    "BasePath": "/telemetry",                 // serve the UI beside another app at "/"
    "ApiBasePath": "/telemetry/api",          // only if the API moved too; "/api" otherwise
    "BrandName": "Acme Watchtower",
    "BrandTagline": "Custom Consumer Branding"
  }
}
```

`BasePath` rewrites the bundle's `<base href>` at startup, which re-roots its assets, its
`config.json` fetch and its client-side router together. Behind a reverse proxy the prefix must be
*forwarded* rather than stripped, and the origin root then returns 404 — see
[the UI package README](src/Keryhe.Telemetry.Ui/README.md#hosting-the-ui-under-a-sub-path). The
same settings are available in code via `AddKeryheTelemetryUi(configuration, options => ...)` or
`app.UseKeryheTelemetryUi(options => ...)`, which take precedence over the configuration section.
The Dashboard's error-rate thresholds are configurable the same way, under
`TelemetryUi:HealthThresholds`; see
[the UI package README](src/Keryhe.Telemetry.Ui/README.md#health-thresholds).

The API is mounted under `Telemetry:Api:BasePath` (default `/api`) and its authorization is configured
under `Telemetry:Api:Authorization` (off by default); see
[the API package README](src/Keryhe.Telemetry.Api/README.md) and
[docs/CONFIGURATION.md](docs/CONFIGURATION.md).

Add `Keryhe.Telemetry.Collector` (plus `AddKeryheTelemetryCollector()`/`MapKeryheTelemetryCollector()`
and the matching `Add<Provider>CollectorServices(configuration)` call, e.g.
`AddTimescaleCollectorServices`) the same way if your host should also ingest OTLP, running it
as its own service (see [the collector README](src/Keryhe.Telemetry.Collector/README.md)). Pin the UI and API packages to the same version —
they ship in lockstep, and a mismatch fails silently (a field goes missing from a rendered page)
rather than with an error.

## Testing

```bash
# Integration tests: every provider against real Testcontainers databases (requires Docker)
dotnet test tests/Keryhe.Telemetry.IntegrationTests
dotnet test tests/Keryhe.Telemetry.IntegrationTests --filter Provider=SqlServer   # one provider

# Test data generator simulation tests (no database or Docker)
dotnet test tests/Keryhe.Telemetry.TestDataGenerator.Tests
```

`tests/Keryhe.Telemetry.StressTests` is a manual, Docker-based load harness (never part of `dotnet test`);
see [its README](tests/Keryhe.Telemetry.StressTests/README.md).

## Documentation

- [docs/SETUP.md](docs/SETUP.md) — full setup, per-provider schema, deployment, alerting and retention
- [docs/CONFIGURATION.md](docs/CONFIGURATION.md) — every configuration key and its default
- [CLAUDE.md](CLAUDE.md) — architecture and implementation notes

## License

[MIT](LICENSE)

## Acknowledgments

Built according to the [OpenTelemetry Protocol Specification](https://github.com/open-telemetry/opentelemetry-proto)
