# Architecture

How Keryhe Telemetry fits together, and how to compose your own hosts from its packages. Setup is in [SETUP.md](SETUP.md); every key is in [CONFIGURATION.md](CONFIGURATION.md).

## Overview

```
OpenTelemetry SDKs (any language)
  → OTLP gRPC (port 5117) → Keryhe.Telemetry.Collector.Server
  → bounded ingestion channel → background worker → provider bulk writer
  → PostgreSQL / SQL Server / MySQL / ClickHouse / Parquet files   (telemetry data)
  → Keryhe.Telemetry.Api.Server (REST API, port 5188 / 7105)
  → Angular SPA (src/telemetry-client, port 4201)
```

Tenants (with their retention windows), API keys and alert rules are the **control plane**: a small relational
database the collector reads for API keys and the API reads and writes for everything else. On
PostgreSQL, SQL Server and MySQL it is simply the same database as the telemetry data; a ClickHouse
or Parquet deployment runs its control plane on one of those three, or (Parquet's natural pairing) in a
SQLite file on the same machine, which needs no database server.

Two providers are chosen at runtime, and both are required: `Database:Provider` for telemetry data
(`PostgreSQL`, `SqlServer`, `MySql`, `ClickHouse`, or `Parquet`, with `ConnectionStrings:Collector` / `:Api`, or `Telemetry:Parquet:RootPath` for Parquet) and
`ControlPlane:Provider` for the control plane (`PostgreSQL`, `SqlServer`, `MySql`, or `Sqlite`, with
`ConnectionStrings:ControlPlane`; for `Sqlite`, `Data Source=<absolute path of the file>`, see
[`Keryhe.Telemetry.Sqlite`](../src/Keryhe.Telemetry.Sqlite/README.md)). Each provider ships its own implementation and is selected by the
host at startup.

| Project | Role |
|---------|------|
| `Keryhe.Telemetry.Core` | Domain interfaces and models, plus the provider-agnostic write repositories, ingestion channel + background worker and Dapper read-repository bases |
| `Keryhe.Telemetry.PostgreSQL` / `.SqlServer` / `.MySql` / `.ClickHouse` / `.Parquet` | Per-provider implementations: telemetry reads and writes, and (PostgreSQL, SQL Server, MySQL) the control plane |
| `Keryhe.Telemetry.Sqlite` | The SQLite control plane (a file on one machine; creates and migrates itself under the API host), for deployments with no database server |
| `Keryhe.Telemetry.Collector` / `.Collector.Server` | OTLP ingestion over gRPC and HTTP with per-tenant API key authentication (class library + thin host); a sample client-side OpenTelemetry Collector configuration is in [`deploy/otel-collector`](../deploy/otel-collector/README.md) |
| `Keryhe.Telemetry.Api` / `.Api.Server` | REST API controllers, base-path routing and authorization, alert evaluation (rules, webhooks, periodic worker) and retention sweeps (class library + thin host) |
| `Keryhe.Telemetry.Ui` | Prebuilt Angular UI, packaged as static web assets — see [Build your own host](#build-your-own-host) below |
| `Keryhe.Telemetry.Admin` | Console tool for tenants and API keys (PostgreSQL, SQL Server, MySQL, SQLite: the control-plane providers) |
| `Keryhe.Telemetry.TestDataGenerator` | Worker service that simulates a multi-tenant e-commerce system and emits realistic traces, logs and metrics (24h backfill, then live) |
| `src/telemetry-client` | Angular 20 SPA source (Dashboard, Traces, Metrics, Logs, Alerts, Settings) — built into `Keryhe.Telemetry.Ui`, not part of the .sln |
| `tests/` | Docker-based per-provider integration tests, a stress-test harness and the test data generator's tests — see [SETUP.md](SETUP.md#testing) |

See [CLAUDE.md](../CLAUDE.md) for a deeper architectural walkthrough (composition roots, ingestion
pipeline, multi-tenancy, provider-specific caveats).

## Build your own host

Every piece of this stack ships as a NuGet package — `Keryhe.Telemetry.Core`, `.Api`,
`.Collector`, `.Ui`, and the five telemetry providers — so you can compose your own ASP.NET Core
hosts instead of running `Keryhe.Telemetry.Api.Server`/`.Collector.Server` as-is: add your own middleware,
combine it with an existing application, or change what gets exposed.

```xml
<ItemGroup>
  <PackageReference Include="Keryhe.Telemetry.Api" Version="2.0.2" />
  <PackageReference Include="Keryhe.Telemetry.Ui" Version="2.0.2" />
  <!-- Plus the provider package for your telemetry data (Database:Provider) and the one for your
       control plane (ControlPlane:Provider). On PostgreSQL, SQL Server or MySQL these are the same
       package; a ClickHouse or Parquet consumer references Keryhe.Telemetry.ClickHouse or .Parquet and one of the others
       (or Keryhe.Telemetry.Sqlite, a file-based control plane that needs no database server). -->
  <PackageReference Include="Keryhe.Telemetry.PostgreSQL" Version="2.0.2" />
</ItemGroup>
```

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddKeryheTelemetryApi(builder.Configuration); // controllers, tenant context, authorization
builder.Services.AddKeryheTelemetryUi(builder.Configuration);  // binds the TelemetryUi section
builder.Services.AddPostgreSqlApiServices(builder.Configuration);              // telemetry reads: ConnectionStrings:Api
builder.Services.AddPostgreSqlControlPlaneApiServices(builder.Configuration);  // alerts, tenants and their retention windows: ConnectionStrings:ControlPlane

var app = builder.Build();

// Paired with an explicit UseRouting() call right after it, so routing does not select an
// endpoint before the UI's static-file middleware runs.
app.UseKeryheTelemetryUi();
app.UseRouting();

// Register your own authentication scheme and policies when you enable
// Telemetry:Api:Authorization; with it off these are no-ops.
app.UseAuthentication();
app.UseAuthorization();

app.MapKeryheTelemetryApi();   // maps the controllers; unknown API paths get a JSON 404
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
[the UI package README](UI.md#hosting-the-ui-under-a-sub-path). The
same settings are available in code via `AddKeryheTelemetryUi(configuration, options => ...)` or
`app.UseKeryheTelemetryUi(options => ...)`, which take precedence over the configuration section.
The Dashboard's error-rate thresholds are configurable the same way, under
`TelemetryUi:HealthThresholds`; see
[the UI package README](UI.md#health-thresholds).

The API is mounted under `Telemetry:Api:BasePath` (default `/api`) and its authorization is configured
under `Telemetry:Api:Authorization` (off by default); see
[the API package README](../src/Keryhe.Telemetry.Api/README.md) and
[docs/CONFIGURATION.md](CONFIGURATION.md).

Add `Keryhe.Telemetry.Collector` (plus `AddKeryheTelemetryCollector()`/`MapKeryheTelemetryCollector()`
and the matching `Add<Provider>CollectorServices(configuration)` and
`Add<Provider>ControlPlaneCollectorServices(configuration)` calls, e.g. `AddPostgreSqlCollectorServices`
and `AddPostgreSqlControlPlaneCollectorServices`) the same way if your host should also ingest OTLP, running it
as its own service (see [the collector README](../src/Keryhe.Telemetry.Collector/README.md)). Pin the UI and API packages to the same version —
they ship in lockstep, and a mismatch fails silently (a field goes missing from a rendered page)
rather than with an error.

## Acknowledgments

Built according to the [OpenTelemetry Protocol Specification](https://github.com/open-telemetry/opentelemetry-proto).
