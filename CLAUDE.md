# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

Requires the .NET 10 SDK and Node.js (Angular 20 for the UI).

```bash
# Build the whole .NET solution
dotnet build Telemetry.sln

# Run the all-in-one host (gRPC ingestion + REST API + Angular UI in one process)
dotnet run --project src/Keryhe.Telemetry.Server

# ...or run the two split hosts separately (scale-out deployments):
# gRPC OTLP ingestion server (write path)
dotnet run --project src/Keryhe.Telemetry.Collector.Server
# REST API (read path, consumed by the Angular UI)
dotnet run --project src/Keryhe.Telemetry.Api.Server

# Run the Angular UI (dev server on http://localhost:4201 — development only)
cd src/telemetry-client && npm install && npm start

# Publish the all-in-one host (also builds + bundles the Angular UI, via Keryhe.Telemetry.Ui)
dotnet publish src/Keryhe.Telemetry.Server -c Release -o ./publish-server
# The API-only host publishes the UI the same way:
dotnet publish src/Keryhe.Telemetry.Api.Server -c Release -o ./publish
# ...and to publish against an already-built src/telemetry-client/dist instead (the flag is on
# Keryhe.Telemetry.Ui, not the host, but propagates transitively through the ProjectReference):
dotnet publish src/Keryhe.Telemetry.Api.Server -c Release -p:BuildSpa=false

# Run the test data generator: simulates three e-commerce tenants and emits them live through the real
# OpenTelemetry SDK. Tenant API keys must already exist in the database and go in user secrets
# (Generator:Tenants:N:ApiKey), never appsettings.
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator
# Backfilling history (hand-built OTLP, then continues live) is opt-in; the window defaults to 24h.
# A second backfill over the same window stores it twice, so truncate the hot tables first.
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator -- --Generator:Backfill:Enabled=true
# e.g. only the last hour, and stop instead of going live:
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator -- --Generator:Backfill:Enabled=true --Generator:Backfill:Window=01:00:00 --Generator:Live=false
# Its tests (no database or Docker; sinks are compared through an in-process fake collector):
dotnet test tests/Keryhe.Telemetry.TestDataGenerator.Tests

# Apply database schema (per provider)
psql -d telemetry -f schema/PostgreSQL-Schema.sql      # PostgreSQL (plain)
psql -d telemetry -f schema/Timescale-Schema.sql       # PostgreSQL + TimescaleDB
sqlcmd -d telemetry -i schema/SqlServer-Schema.sql     # SqlServer
clickhouse-client --database telemetry --multiquery < schema/ClickHouse-Schema.sql  # ClickHouse
mysql telemetry < schema/MySQL-Schema.sql               # MySQL

# Or use the runner (skips if the target schema_version is already applied):
schema/apply-schema.sh <postgresql|timescale|sqlserver|clickhouse|mysql> [database]
```

**Schema 3.0.x is a fresh-install schema.** There is no migration from 2.x (decision 4 of
`plans/schema-simplification.md`, which explains every change below): an existing 2.x database must
be recreated, and `schema/migrations/` no longer exists. `apply-schema.sh` only skips when the 3.0.1
version row is already recorded. 3.0.1 over 3.0.0 adds one thing: ClickHouse's `bloom_filter` skip index on
`spans.trace_id` (`plans/trace-list-detail-performance.md`, Phase 5); an existing 3.0.0 ClickHouse database
can take it with `ALTER TABLE spans ADD INDEX idx_spans_trace trace_id TYPE bloom_filter(0.01) GRANULARITY 4`
(and `MATERIALIZE INDEX` for existing parts).

There is one schema script per supported provider, all producing the same logical
table/column set: `schema/PostgreSQL-Schema.sql` (plain Postgres), `schema/Timescale-Schema.sql`
(Postgres + TimescaleDB hypertables and compression),
`schema/SqlServer-Schema.sql`, `schema/MySQL-Schema.sql`, and `schema/ClickHouse-Schema.sql`
(columnar MergeTree family; see the ClickHouse notes below). A schema change edits all five plus
`TARGET_VERSION` in `apply-schema.sh`, in one commit. Production topology is the collector/API split;
the all-in-one `Keryhe.Telemetry.Server` is for development and is not the topology that is measured.

`tests/Keryhe.Telemetry.IntegrationTests` (xUnit) runs every provider's real
`Add<Provider>CollectorServices`/`Add<Provider>ApiServices` registrations against real
Testcontainers-managed databases, applying the actual `schema/*.sql` scripts and seeding through
`ITelemetryBulkWriter` (not `Keryhe.Telemetry.TestDataGenerator`, whose OTLP/gRPC-based data is
time-dependent and not hand-computable — see `plans/list-pages-server-side.md`'s Phase 0 section). Requires
Docker. The container startup, schema application and tenant/API-key seeding these fixtures use
live in `tests/Keryhe.Telemetry.TestInfrastructure` (one `ProviderContainer` per provider, plus
`TenantSeeder` for N tenants), which the stress harness (below) shares;
it also offers opt-in per-provider diagnostics and CPU/memory
limits (and an optional `CpusetCpus` pin) via `ContainerOptions`, which the integration fixtures leave off.

Beyond the read-path tests, the suite covers per-provider retention (`RetentionTestsBase`: 200- and
100-day-old rows, an hour either side of the cutoff, current — Timescale and ClickHouse drop whole
chunks/days so their "hour before the cutoff" row is not asserted), the trace-list anchor semantics
(`TracePhase3TestsBase`), `GET /api/capabilities`' shape, SQL Server's `READ_COMMITTED_SNAPSHOT` and
id-parameter plan (no `CONVERT_IMPLICIT`), and PostgreSQL/Timescale's index-not-seq-scan plan, each
negative-controlled.

```bash
# All five providers (one xUnit collection fixture per provider, containers started once per run)
dotnet test tests/Keryhe.Telemetry.IntegrationTests

# One provider only (Provider trait: PostgreSQL | Timescale | SqlServer | MySql | ClickHouse)
dotnet test tests/Keryhe.Telemetry.IntegrationTests --filter Provider=SqlServer
```

The Angular project has `npm test` (Karma/Jasmine) but no meaningful tests are set up.

### Stress tests

`tests/Keryhe.Telemetry.StressTests` is a manual, Docker-based harness (never part of `dotnet test`) that ingests OTLP load into each provider under both
host topologies while headless Chromium walks the UI, then reports write/read latency, locking, resource use, slowest SQL and a data correctness check as
`result.json`, `report.html` and (for a matrix) `comparison.html`. Its README covers prerequisites, profiles, ramp criteria and how to read the report;
the design is in `plans/stress-tests.md`. It reuses the Collector's generated gRPC stubs, which is why the three `*_service.proto` entries in the
Collector csproj are `GrpcServices="Both"`.

```bash
# One-time: fetch Chromium for the browser tour
dotnet run --project tests/Keryhe.Telemetry.StressTests -- playwright-install

# Run scenarios (provider/topology/profile each take a value or "all"; runs sequentially)
dotnet run --project tests/Keryhe.Telemetry.StressTests -- run --provider PostgreSQL --topology allinone --profile smoke
dotnet run --project tests/Keryhe.Telemetry.StressTests -- run --provider all --topology all --profile smoke
dotnet run --project tests/Keryhe.Telemetry.StressTests -- run --provider SqlServer --scenario ramp
# The provider's write ceiling with nothing reading (no browsers, no marker probes; stops on commit lag,
# gate wait, drops and errors). Run it next to "ramp" on one commit: the difference is what reads cost writes.
dotnet run --project tests/Keryhe.Telemetry.StressTests -- run --provider SqlServer --topology split --profile ramp-write-only

# Rebuild the report from a finished run (output is in the gitignored stress-results/)
dotnet run --project tests/Keryhe.Telemetry.StressTests -- report --in stress-results/<timestamp>
```

`--seed-days <n>` (1-60) with `--seed-spans-per-day <n>` sends backdated history through the real OTLP path before the warm-up (so the spans table
holds many Timescale chunks / ClickHouse partitions) plus three large traces (1,000/5,000/20,000 spans) for the first tenant, ledgers them as current
(the correctness check still balances), tags the profile name (`...-seed14d`), and after quiesce probes trace detail for them with and without the
`?start=&end=` hint (the report's "History seed" section). A run with history is not comparable with one without. A database that stops answering under
overload (ClickHouse is OOM-killed at its 8 GB limit above about 21,000 records/s on the reference machine) is recorded as a `DatabaseOutage` finding
(`docker inspect`: status, exit code, OOM-killed) and the steps that need it are skipped; the scenario still ends `error: none`. Removing the database
container at the end is logged, not fatal. Any other `OperationCanceledException` is reported with its exception, never as "Cancelled." (that is Ctrl-C only).

The write path it measures is instrumented on `IngestionMetrics` (see "Write path decoupling" below), including `commit_lag` (enqueue to commit), the
signal a write-only ramp stops on because it does not depend on any read query; `RetentionWorker`'s "Retention sweep complete" log
line carries the sweep's elapsed milliseconds for the same reason. The report lists every API route's server-side latency apart from the write
instruments, the comparison page pairs each write-only ramp with its full ramp (the isolation number), and the correctness ledger's
"a re-delivered span collapses" flag follows the schema under test (true on 2.x, false on 3.0.0, read from `apply-schema.sh`'s `TARGET_VERSION`).
`--db-cpuset <cpus>` pins the database container to CPUs of the Docker VM and the report says what the database shared CPUs with; pointing the
harness at a database on another machine is not supported yet.

### Query benchmark (lab)

`tests/Keryhe.Telemetry.IntegrationTests/Tests/TraceQueryBench.cs` is an opt-in benchmark (a no-op unless
`TRACE_BENCH_OUT` is set) that seeds a stress-harness-sized volume (default 60,000 traces of ~10 spans over six hours;
`TRACE_BENCH_TRACES`) into each provider's container and times the repository's real trace queries (summary, first/next/last
page, errors, slow, search, operation, samples, detail with and without the start hint) to one table. It is for attributing
a query change quickly; the stress harness remains the measure of record because it carries concurrent ingest and reads.
**Run providers one at a time** (xUnit starts every provider's collection in parallel, which contaminates the timings):
`TRACE_BENCH_OUT=/tmp/b.txt TRACE_BENCH_LABEL=x dotnet test tests/Keryhe.Telemetry.IntegrationTests --no-build --filter "Provider=PostgreSQL&FullyQualifiedName~TraceQueryBench"`,
once per provider. `TRACE_BENCH_SQL=<file>` additionally times raw SQL variants (separated by `-- ### name`) over the same data.

### Test data generator

`src/Keryhe.Telemetry.TestDataGenerator` simulates a small e-commerce system (web-frontend → api-gateway →
user/catalog/cart/order services → inventory/payment, with Postgres, Redis, a message queue, Stripe and an
email provider behind them) for each configured tenant, and sends it to the collector. The design and
decisions are in `plans/test-data-generator-realism.md`. The shape worth knowing before touching it:

- **One simulation core, two sinks.** `TenantSimulator` produces a neutral model (`Model/SimModel.cs`: span
  trees with their logs and measurements attached, background logs, periodic samples) for any stretch of
  *virtual* time. Every random decision is seeded from (seed, tenant, time), so the same range always gives
  the same telemetry however it is chunked. Flows (`Flows/Journeys.cs`) build spans through `SpanScope`, whose
  cursor makes durations nest by construction and propagates failures up the call chain.
- **Backfill is hand-built OTLP (`Sinks/OtlpSink`).** The SDK cannot backdate logs (stamped at emit) or metrics
  (stamped at export, cumulative from process start), so history goes straight to the Collector's generated
  gRPC client stubs, with cumulative metrics from `MetricAggregator`, retry/backoff on backpressure and one
  chunk (default 60 s) per metric interval. 24 h at the default volume takes a few minutes.
- **Live is the real SDK (`Sinks/SdkSink`).** One tracer/meter/logger provider set per pod, authenticated as the
  tenant. Spans are started with explicit start/end times; logs and measurements are recorded while their span is
  the current `Activity`, which is how the SDK attaches trace ids and exemplars. A processor re-stamps each log
  record with the simulated time. Live emission trails the clock by `LiveLagSeconds` so a request has finished.
  A provider listens by source/meter *name*, so scope names are per pod and tenant (`<pod name>`), not a library name.
- **Tenants and incidents.** `Generator:Tenants` (name, scale, API key); the same topology at each tenant's own
  traffic level. `Generator:Incidents` are recurring daily windows in UTC (latency multiplier, extra error
  rate, optional version change, which replaces the service's pods like a rollout and restores them on rollback).
  Traffic follows a daily curve (peak 20:00 UTC), a weekend lift and per-minute bursts.
- **Keys.** The generator does not create tenants or keys; they must exist (`api_keys.key_hash` is the
  lowercase-hex SHA-256 of the bearer token). `Validate()` fails fast naming the missing user-secret.
- **Guard rails.** `tests/Keryhe.Telemetry.TestDataGenerator.Tests` checks the simulation invariants (trees nest, a
  failed child fails its parent, consumer traces link to producers, logs belong to their span) and runs the same
  chunks through both sinks into an in-process fake collector, asserting the two look alike (spans, resources,
  logs, metric names/units/types/attributes, exemplars). Change a sink and run them.
- Re-running a backfill over a window already ingested stores those spans a second time (spans are plain appends,
  and the simulation is deterministic, so the ids repeat). Truncate the hot tables first (leave the
  reference tables alone: the host's `ResourceScopeCache` would then point at rows that no longer exist).

### API base path, routes and authorization

`Keryhe.Telemetry.Api` is meant to be mounted inside a consumer's host
(`plans/api-base-path-authorization.md`; the full consumer setup is its README). The shape worth knowing:

- **Base path.** `Telemetry:Api:BasePath` (`TelemetryApiOptions`, default `/api`; `/`/empty rejected at startup)
  prefixes every route. Controllers declare only their own segment (`[Route("traces")]`); `TelemetryApiConvention`
  (an `IApplicationModelConvention`, this assembly's controllers only) adds the base path, plus
  `tenants/{tenantId:long:min(1)}` for `[TenantScoped]` controllers (`Traces`, `Logs`, `Metrics`, `Resources`,
  `Alerts`; `Capabilities`, `Tenants`, `Settings` are global) and the authorization filter.
  `MapKeryheTelemetryApi()` (replaces `MapControllers()`) also maps `{base}/{**rest}` to a JSON 404 so an unknown API
  path is not the SPA shell. `UseKeryheTelemetryApi()` is an `[Obsolete]` no-op: `TenantMiddleware` and `X-Tenant-Id`
  are gone, the tenant is the route's `tenantId`.
- **Authorization** (`Telemetry:Api:Authorization`, off by default). `TelemetryAuthorizationFilter` calls
  `IAuthorizationService` with `TelemetryOperationRequirement` (each action carries exactly one
  `[TelemetryOperation]`: Read/Export/ManageAlerts/ManageSettings, never inferred from the HTTP method; a test
  enumerates every action) and, on tenant routes, `TenantAccessRequirement`, resource `TelemetryResource(tenantId)`.
  Built-in handlers: `PolicyMappedOperationHandler` (operation -> a host-registered policy by name; Export falls back
  to Read, ManageAlerts/ManageSettings to Admin, Read/Admin to the default policy) and
  `ClaimMappedTenantAccessHandler` (`TenantMappings`: claim -> tenant names/ids/`"*"`, names resolved through
  `ITenantCatalogRepository`, cached 30 s). A consumer adds ordinary handlers (`context.Fail()` narrows). 401 when
  unauthenticated, 403 otherwise (a tenant refusal adds `X-Telemetry-Denied: tenant`, which the UI's
  "no access to this tenant" keys on; an operation refusal does not); a `POST/PUT/DELETE` without a *bearer* token
  needs `X-Telemetry-Client: 1` (CSRF; Basic/Negotiate do not exempt, browsers send those unprompted). A policy that
  names `AuthenticationSchemes` is authenticated through `IPolicyEvaluator` first (`TelemetryPolicyResolver` maps the
  operation to its policy), as the ASP.NET authorization middleware does.
  `TelemetryApiStartupValidator` fails startup on undefined policies, no authentication scheme, malformed mappings, or
  nothing able to grant tenant access, and warns when unauthenticated outside Development / Admin unset.
  `AlertService.EvaluateAllAsync` sets `ITenantContext` directly, outside any request, so the filter never applies to it.
- **The in-repo hosts are development examples** with no sign-in: they call `UseAuthentication()`/`UseAuthorization()`
  (no-ops without a scheme) and `MapKeryheTelemetryApi()` and run with authorization disabled. The HTTP behaviour is tested
  in-process by `tests/Keryhe.Telemetry.IntegrationTests/ApiHttp` (`--filter Suite=ApiHttp`; `TestServer`, fake
  repositories, a header-driven test scheme, no Docker). A route whose repository is not faked answers 500 once it
  clears authorization, which those tests read as "authorized".
- **The UI** follows the API: with `TelemetryUi:ApiBasePath` unset it takes `Telemetry:Api:BasePath`. Its pages are at
  `/t/:tenantId/...` (`tenantRouteGuard` sets `TenantService.routeTenantId` before the page exists; localStorage keeps
  only "last used", used by the redirects of the old tenant-less URLs); API services build URLs with
  `tenantApiUrl(apiUrl, tenantId)`. `TelemetryUi:Auth` (`cookie` default, or `oidc` via `angular-auth-oidc-client`,
  imported on demand) reaches the SPA in `config.json`; `authInterceptor` (API URLs only) adds `X-Telemetry-Client`,
  the bearer token or credentials, handles 401 (sign in) and 403 on a tenant route ("no access to this tenant").
  `downloadBlob` stays a fetch so that interceptor runs for exports.

### Default ports

- gRPC ingestion (`Keryhe.Telemetry.Collector.Server`): `http://localhost:5117` (h2c), `https://localhost:7057` (HTTP/2)
- REST API (`Keryhe.Telemetry.Api.Server`): `http://localhost:5188`, `https://localhost:7105` — also serves the UI at `/` when published
- Angular dev server (`src/telemetry-client`): `http://localhost:4201` — **development only**

`Keryhe.Telemetry.Server` (all-in-one) serves all four of the above on the same ports, via named
Kestrel endpoints in its `appsettings.json`: `Grpc` (5117, h2c/`Http2`), `GrpcTls` (7057, `Http2`),
`Api` (5188, `Http1`), `ApiTls` (7105, `Http1AndHttp2`). Its `launchSettings.json` deliberately sets
**no** `applicationUrl` — `ASPNETCORE_URLS` overrides `Kestrel:Endpoints` wholesale and would
collapse the per-endpoint `Protocols`, breaking h2c gRPC on 5117. It also omits `UseHttpsRedirection()`
for the same reason.

The API location is **runtime**, not build-time, configuration: the client fetches
`GET /config.json` before it bootstraps (`src/telemetry-client/src/app/core/config/load-config.ts`)
and reads `apiUrl` from it, rather than the response being baked into the compiled bundle via an
Angular `fileReplacements` swap (there used to be one; there is no `environments/` folder anymore).
This is what lets one published bundle work regardless of where a given host mounts the API —
required once the UI ships as the standalone `Keryhe.Telemetry.Ui` package (see below), since a
consumer bundling it can't rebuild it to point at their own API path. The Angular dev server
(`npm start`) proxies `/api` to `https://localhost:7105` (`src/telemetry-client/proxy.conf.json`),
so development and production both resolve the API at the same relative path, `/api` — that
same-origin shape is also why the API host's CORS policy is no longer load-bearing for local
development (it still matters for a UI hosted on a different origin than its API).

### UI hosting

The Angular UI ships as `Keryhe.Telemetry.Ui`, a Razor class library packaging the compiled SPA
as static web assets — the same NuGet-package story as the other class libraries, so a consumer
building their own host from the `Keryhe.Telemetry.Api`/`.Collector` packages gets the UI too,
without cloning `src/telemetry-client` or installing Node (see
[plans/ui-packaging-runtime-config.md](plans/ui-packaging-runtime-config.md)). Its own csproj
builds `src/telemetry-client` (`npm ci`/`npm run build`) and stages `dist/telemetry-client/browser`
into *its own* `wwwroot` — incrementally (a stamp file plus MSBuild `Inputs`/`Outputs` skip the
npm build once it's already current) and gracefully (a missing Node toolchain warns and packages
an empty UI rather than failing the solution build). `Keryhe.Telemetry.Api.Server` and
`Keryhe.Telemetry.Server` reference it via a plain `ProjectReference`; nothing else needs to know
it exists.

The package has the same `Add*`/`Use*` split as the API and collector sides.
`builder.Services.AddKeryheTelemetryUi(configuration, configure?)` binds `TelemetryUiOptions` from
the **`TelemetryUi`** configuration section (the `AddRetention`/`AddAlerting` shape) and registers
the `TelemetryUiShell` singleton; it is **required** — `UseKeryheTelemetryUi()` throws a
fail-fast `InvalidOperationException` naming it otherwise. That guard is deliberately on the shell
singleton rather than on `IOptions<TelemetryUiOptions>`: the options open generic is registered by
the host builder regardless, so guarding on it would resolve successfully and silently hand back
defaults. Config binding is what lets a consumer relocate or rebrand a *prebuilt* bundle without
recompiling, which is the whole premise of shipping it prebuilt.

`app.UseKeryheTelemetryUi()` serves the packaged bundle at `TelemetryUiOptions.BasePath` — `/` by
default — rather than the Razor class library default of `/_content/Keryhe.Telemetry.Ui/`, and
answers `GET {BasePath}/config.json` with the host's configured options: API location plus
`BrandName`/`BrandTagline`, the consumer-facing product name and tagline shown in the header bar
and, via the Angular client's `BrandedTitleStrategy`, in every route's browser tab title. All
default to this UI's own out-of-the-box branding ("Sentinel" / "OpenTelemetry Visualization"), so a
host that sets nothing sees exactly what it always has. `config.json` also carries the optional
`TelemetryUi:HealthThresholds` overrides for the Dashboard's error-rate bands, only the keys
that are set (the defaults live in `health-thresholds.ts`, merged over in `main.ts` by
`resolveHealthThresholds`; `TelemetryUiOptions.Validate()` fails startup on a bad value). It must
run before authentication,
so UI asset requests — `config.json` included — are public, and it also
negotiates `.br`/`.gz` variants that the SDK generates automatically at the *host's* publish
(`Keryhe.Telemetry.Ui` itself has no compressed variants — `MapStaticAssets()` would give that
negotiation for free but can't be re-rooted for a referenced class library's assets, so
`TelemetryUiApplicationBuilderExtensions` reimplements just that piece).

**`BasePath` and the in-memory shell (`TelemetryUiShell`).** The built `index.html` carries
`<base href="/">`, and *every* asset reference in it, the client's own relative `config.json` fetch
(`load-config.ts`) and its router's `PathLocationStrategy` all resolve against that one attribute —
so serving the file as-is pins the whole UI to the origin root. `BasePath` is therefore implemented
by rewriting that attribute once at startup and serving the result **from memory**, never through
`UseStaticFiles`/`MapFallbackToFile`, which would hand back the unpatched packaged file. Consequences
worth knowing before touching this code:

- `{BasePath}`, `{BasePath}/` **and `{BasePath}/index.html`** are all intercepted by the shell
  middleware. The third is not optional — left to `UseStaticFiles` it would serve the unpatched
  file. Handling it there also means the compression-negotiation middleware never sees `index.html`,
  so it needs no special case for the one file whose `.br` sibling must not be served.
- Serving from memory means the shell owns what those middlewares gave for free: `Content-Type`,
  `Content-Length` (**the encoded length** — setting the identity length on a compressed body is
  `ERR_CONTENT_DECODING_FAILED` by another route), `Cache-Control: no-cache`, `Vary`, a **distinct
  strong ETag per representation** (identity/`-br`/`-gz`), and `If-None-Match` → 304. The 304 branch
  returns before any content header is written.
- `MapKeryheTelemetryUiFallback()` is **scoped** to `{BasePath}/{*path:nonfile}` and must be
  `MapFallback` specifically, whose `Order = int.MaxValue` is what keeps `api/*` and the gRPC routes
  winning. It reads the effective base path back off the shell and throws if `UseKeryheTelemetryUi`
  has not run, so mapping the two out of order fails loudly instead of registering deep links at the
  wrong prefix. Unlike the `MapFallbackToFile` it replaced it *does* negotiate compression, since
  the shell is already held in all three representations.
- `BasePath` is fixed at startup, not derived per request from `PathBase`. A reverse proxy must
  therefore **forward** the prefix, not strip it, and a non-empty value means `GET /` returns 404.
- `NormalizeBasePath` validates against `^(/[A-Za-z0-9._~-]+)+$` rather than only trimming: the
  normalized value is concatenated into a route-pattern literal, where `{`/`}`/`?`/`#`/`%` would
  produce a corrupt template.
- A build with no Node toolchain packages no `index.html` at all (the csproj warns rather than
  failing). `TelemetryUiShell.Content` is nullable for exactly that case and must degrade to a 404,
  never an exception.

**The host must still call `app.UseRouting()` explicitly, immediately after
`UseKeryheTelemetryUi()`** — left to `WebApplication`'s own implicit insertion, it lands ahead of
that method's middleware (since a `Map*` call exists later in every such host), so routing
pre-selects an endpoint before the UI's own static-file middleware runs, and `UseStaticFiles`
correctly defers to an already-selected endpoint rather than serving. The shell itself is no longer
exposed to this (it is written from memory, ahead of routing, without consulting the selected
endpoint — which is what retired the old `net::ERR_CONTENT_DECODING_FAILED` symptom on `/`), but
every other asset still is, and the implicit insertion also silently moves `UseCors` and
`UseAuthentication`/`UseAuthorization` to the wrong side of routing.

Because static web assets flow through a plain `ProjectReference` at *build* time, not only at
publish, `dotnet run --project src/Keryhe.Telemetry.Api.Server` now serves the UI too — unlike the
former per-host `BuildAngularClient`/`IncludeAngularClient` MSBuild targets this replaced, where
`wwwroot` was genuinely empty in development. The Angular dev server (`npm start`) remains the
tool for UI development (HMR); this is what a consumer following the README will actually run.

## Architecture

This is an **OpenTelemetry (OTLP) ingestion and visualization platform** — a self-hosted
alternative to tools like Jaeger or Grafana Tempo. It receives telemetry via gRPC, stores it
in a database (PostgreSQL, TimescaleDB, SQL Server, or ClickHouse), and exposes it through a
REST API consumed by an Angular single-page application.

### Projects

| Project | Role |
|---------|------|
| `Keryhe.Telemetry.Core` | Domain interfaces and models shared across projects (no infrastructure deps) |
| `Keryhe.Telemetry.Data` | Provider-agnostic pieces: thin write repositories, ingestion channel + worker, Dapper read repository bases, helpers |
| `Keryhe.Telemetry.PostgreSQL` | Plain-Postgres provider implementation (Npgsql + Dapper) |
| `Keryhe.Telemetry.Timescale` | TimescaleDB provider implementation |
| `Keryhe.Telemetry.SqlServer` | SQL Server provider implementation (Microsoft.Data.SqlClient + Dapper) |
| `Keryhe.Telemetry.ClickHouse` | ClickHouse provider implementation (ClickHouse.Client bulk-copy writes + Dapper reads) |
| `Keryhe.Telemetry.MySql` | MySQL provider implementation (MySqlConnector + Dapper) |
| `Keryhe.Telemetry.Collector` | gRPC services + OpenTelemetry proto files → generated stubs (class library) |
| `Keryhe.Telemetry.Collector.Server` | Thin ASP.NET Core host that maps the gRPC services and runs the ingestion worker |
| `Keryhe.Telemetry.Api` | REST API controllers, base-path routing, authorization, and read-service wiring (class library) |
| `Keryhe.Telemetry.Api.Server` | Thin ASP.NET Core host that composes the API + OpenAPI + CORS |
| `Keryhe.Telemetry.Server` | All-in-one host: gRPC ingestion + REST API + Angular UI in one process |
| `Keryhe.Telemetry.Ui` | Prebuilt Angular UI, packaged as static web assets (Razor class library; no .razor/.cshtml) |
| `Keryhe.Telemetry.Alerting` | Alert rule evaluation with pluggable evaluators and webhook delivery |
| `Keryhe.Telemetry.TestDataGenerator` | Worker service that simulates a multi-tenant e-commerce system and emits it as OTLP: hand-built OTLP for backfill, the OpenTelemetry SDK for live (see "Test data generator") |
| `src/telemetry-client` | Angular 20 UI source (Angular Material, ApexCharts, ngx-graph) — not part of the .sln; built by `Keryhe.Telemetry.Ui`, not by any host directly |

> The former `Keryhe.Telemetry.Server` (monolithic gRPC host) and `Keryhe.Telemetry.Client`
> (Blazor UI) have been removed. Stale `bin`/`obj` directories may remain on disk but are not
> in the solution. The stack migrated from **EF Core to Dapper** — there are no `DbContext`
> classes anymore.

### Data Flow

```
OpenTelemetry SDKs (any language)
  → OTLP gRPC (port 5117) → Keryhe.Telemetry.Collector (LogService/TraceService/MetricService)
  → thin write repos (Data) enqueue → TelemetryIngestionChannel (gated on resident record/span count)
  → TelemetryIngestionWorker (background) → ITelemetryBulkWriter (active provider) → DB

Angular UI (localhost:4201)
  → REST (Keryhe.Telemetry.Api.Server, /api) → controllers → I*ReadRepository (active provider, Dapper) → DB
```

### The three composition roots

All three hosts are thin `Program.cs` shells; the real wiring lives in the class libraries,
behind two matching extension pairs:

- **Write side** — `AddKeryheTelemetryCollector(configuration)` / `MapKeryheTelemetryCollector()`
  (`Keryhe.Telemetry.Collector/TelemetryCollectorExtensions.cs`): gRPC, the ingestion channel,
  `ResourceScopeCache`, the `TelemetryIngestionWorker`, and the write repositories; then maps the
  three gRPC services. It registers no database provider itself.
- **Read side** — `AddKeryheTelemetryApi(configuration)` / `UseKeryheTelemetryApi()`
  (`Keryhe.Telemetry.Api/TelemetryApiExtensions.cs`): controllers (via an MVC application part,
  since they live in the class library) and the tenant context. It likewise registers no
  database provider itself.

Neither class library references any provider project — each host separately calls the active
provider's own `Add<Provider>CollectorServices`/`Add<Provider>ApiServices` (see "Provider
abstraction" below), which is what lets a consumer depend on only the one provider package they
actually use instead of all five.

`Keryhe.Telemetry.Collector.Server` calls the first pair (plus its own provider registration),
`Keryhe.Telemetry.Api.Server` the second (plus its own), and `Keryhe.Telemetry.Server` calls
**both** plus `AddAlerting`, the SPA static-file middleware, and a single provider registration
shared by both sides.

> **All-in-one constraint.** The Npgsql-backed providers (`PostgreSQL`, `Timescale`) register a
> singleton `NpgsqlDataSource` in *both* `Add*CollectorServices` (from `ConnectionStrings:Collector`) and
> `Add*ApiServices` (from `ConnectionStrings:Api`). In one container the last registration silently
> wins for both paths, so `Keryhe.Telemetry.Server` fails fast at startup
> (`Program.EnsureSingleNpgsqlDataSource`) if the two connection strings differ. SqlServer,
> ClickHouse, and MySql read their connection string per class and are unaffected.

### Provider abstraction (the central pattern)

The database provider is selected at runtime by the **`Database:Provider`** config key
(`"PostgreSQL"`, `"Timescale"`, `"SqlServer"`, `"ClickHouse"`, or `"MySql"`). Each provider project exposes
`ServiceCollectionExtensions` with `Add<Provider>CollectorServices` / `Add<Provider>ApiServices`,
and the hosts `switch` on the config key to call the right one. An unknown/missing provider
throws at startup.

**ClickHouse provider notes.** ClickHouse is columnar/OLAP, so the provider diverges from the
relational four in a few deliberate ways (all confined to the provider; Core interfaces are
unchanged):
- **App-generated ids.** No auto-increment / `RETURNING`. `ClickHouseBulkWriter` computes the
  `Int64` surrogate keys the read repos join on, always from the table's full `ORDER BY` key:
  resource ids from `(tenant_id, resource_hash)`, scope ids from `scope_hash` (scopes carry no
  tenant) and metric ids from `(resource_id, scope_id, name, type)`, all via `ClickHouseIds.FromKey`.
  `spans.id` and `log_records.id` come from a monotonic in-process generator (`RowId`): they are
  only the keyset-paging tiebreak. A span's events and links are `events_json`/`links_json` columns
  on the span row.
- **Reference tables are `ReplacingMergeTree`; spans are not.** resources/scopes/metrics (and the
  control-plane tables) collapse on their `ORDER BY` key at merge time, backed by
  `ResourceScopeCache` + per-batch dedup, so their dedup is *eventual*. `spans` and `log_records`
  are plain `MergeTree` appends (decision 7: a re-delivered span is stored twice and reads tolerate
  it), so there is no `LIMIT 1 BY` and no `FINAL` anywhere on the read path.
  Two consequences are load-bearing: the writer puts an id in `ResourceScopeCache` only AFTER the row's
  insert succeeded (caching first would leave a failed insert — e.g. the server's memory limit under
  overload — cached forever, and every later flush would skip a catalog row that does not exist); and the
  read repositories build their id lookups with `ToDictionaryFirst`, because two flushes that both missed
  the cache can store the same resource/scope/metric id twice until a merge, and a plain `ToDictionary`
  over those rows threw and turned every logs page into a `400` (`ClickHouseDuplicateReferenceRowTests`).
- **Laid out for the way it is read (schema 3.0.0).** `spans` is `ORDER BY (tenant_id, service_name,
  start_time_unix_nano)` and `log_records` `ORDER BY (tenant_id, service_name, time_unix_nano)`, both
  partitioned by day, so a tenant/service/time window prunes partitions and granules. `trace_index` (an
  `AggregatingMergeTree` fed by a materialized view from `spans`, one row per tenant/trace/day) holds
  each trace's min/max start: trace detail and a trace-list page's follow-up query read it for time
  bounds first (`ResolveTraceTimeBoundsAsync`, or the caller's start-time hint when it has one, see "Trace
  detail"), then read `spans` with tenant and time bounds, instead of scanning for a trace id. `spans` and
  `log_records` each have one `bloom_filter` skip index on `trace_id` (logs-by-trace; trace detail, schema 3.0.1),
  which prune the granules inside those bounds: the sort key has no trace-id seek, so a bounded read otherwise
  reads at least one granule per service. Where null carries no meaning the column is non-`Nullable` with an empty default
  (`parent_span_id`, `trace_state`, `status_message`, `event_name`, log `trace_id`/`span_id`,
  `service_name`) and the read repositories map `''` back to null.
- **Writes go through `ClickHouseBulkCopy`** (async batched insert), one long-lived instance
  cached per destination table for the life of the process (`ClickHouseBulkWriter`'s singleton
  `TableBulkCopy` cache) rather than a fresh connection + `InitAsync()` schema-probe round trip on
  every flush; reads reuse the shared Dapper bases unchanged (attributes are JSON text
  deserialized in C#).
- **Retention is `ALTER TABLE ... DROP PARTITION`** for every fully expired day (no row deletes, no
  mutations); see "Retention".
- **Control-plane is best-effort.** Alert-rule CRUD uses `ALTER TABLE ... UPDATE` mutations and
  `TryClaimFireAsync` is NON-ATOMIC (read-check-then-update), so under concurrent evaluators a
  rule could double-fire. Acceptable because no host currently drives scheduled evaluation.
- **The anchors derived table is a `GROUP BY trace_id`** with `argMin` over `(start, id)` (see "Trace
  anchors"). ClickHouse resolves a SELECT alias over a same-named column in WHERE, so the aggregates are
  computed under non-colliding aliases in an inner query and renamed outside it.

**Search and capabilities (schema 3.0.0, decisions 3 and 19).** Free-text and `key:value` search is
unindexed and time-window bounded on **every** provider: one predicate form per dialect (the
`LIKE`/`ILIKE` text form and `LOWER(col ->> key)` / `JSON_VALUE` / `JSON_EXTRACT` /
`JSONExtractRaw` attribute comparison, all case-insensitive), no GIN/trigram/skip indexes, and a
search or `mode=slow` request over a window longer than `Telemetry:Query:RawSearchWindowHours`
(default 24) is rejected (`400`) by `RawSearchWindowGuard`. The provider tiers (`ProviderTier`) and
`IndexedSearch` are gone. `ProviderCapabilities` keeps `RawSearchWindowHours`, `ExportMaxWindowDays`
(7 on PostgreSQL/Timescale/ClickHouse, 1 on SQL Server/MySQL), `AsOfBackoffSeconds` and
`ExemplarPaging` (the metric detail page uses it to choose keyset paging vs newest-500), and
`GET /api/capabilities` reports them; the Angular client reads it once at startup
(`CapabilitiesService`) and reuses it everywhere a page needs a limit.

**MySQL is MySQL 8.0.19+ only** (no MariaDB): upserts use the `INSERT ... VALUES (...) AS new ON
DUPLICATE KEY UPDATE col = new.col` alias form.

### Key Patterns

**Write path decoupling — `TelemetryIngestionChannel`** (Data, singleton): three unbounded
`System.Threading.Channels` (one per signal type, `SingleReader = false`). Backpressure is not the
channel's own capacity but a paired `RecordCountGate` per signal, bounding resident RECORDS (spans,
not traces, for the trace signal) rather than resident batches — an OTLP export's size is entirely
client-controlled, so a batch-count bound does not actually cap memory. Write repositories
(`TraceWriteRepository`, etc.) call `gate.AcquireAsync` before enqueuing and return — they no
longer carry a retention `Delete*` passthrough (see the retention notes below for where that
lives now). The Traces channel specifically carries flat `List<SpanModel>`,
not `List<TraceModel>`: `TraceWriteRepository` flattens each trace's spans and resolves each span's
effective resource/scope (its own override, else its trace's) once, at that single point, so
`ITelemetryBulkWriter.FlushTracesAsync` and every provider behind it read an already-flat,
already-resolved span list with no grouping to unwrap and no fallback to re-apply.
`TelemetryIngestionWorker` runs `FlushConcurrency` concurrent
drain loops per signal (`TelemetryIngestionOptions`, section `Telemetry:Ingestion`) so DB write
latency overlaps instead of one flush blocking the next; the in-memory drain step itself stays
serialized per signal via an internal lock, since interleaving it across loops would corrupt the
batch-size accounting the gate release depends on. Each drained batch is merged up to a configurable
per-signal size, flushed through the active provider's `ITelemetryBulkWriter` with bounded
exponential-backoff retry (`MaxFlushRetries`), and the gate is released for what was drained
regardless of outcome. A batch that still fails after retries are exhausted is dropped and recorded
on `IngestionMetrics`'s `records_dropped` counter — the three gRPC `Export` methods' partial-success
responses reflect only enqueue success, never this later, asynchronous drop; each documents that
explicitly. This isolates gRPC latency from DB write latency and provides backpressure.

The write path is instrumented on `IngestionMetrics`'s `Keryhe.Telemetry.Ingestion` meter, every
instrument tagged by `signal` (`logs`/`traces`/`metrics`): `records_dropped` (counter),
`gate_wait` (histogram, ms — time `RecordCountGate.AcquireAsync` spent waiting, the backpressure
signal), `resident_records` (observable gauge — each gate's current count, registered by
`TelemetryIngestionChannel`, which owns the gates), `flush_duration` (histogram, ms, extra tag
`outcome` = `ok`/`failed`, one measurement per flush *attempt*), `flush_retries` (counter),
`records_flushed` (counter), `flush_batch_size` (histogram, one measurement per merged batch) and
`commit_lag` (histogram, ms — enqueue to the commit of the flush that persisted it, one measurement per
export; the repositories stamp each export with `TelemetryIngestionChannel.MarkEnqueued`, a weak side
table keyed by the export's list, and the worker reads it back — this is the write-path health signal,
independent of any read query).
All are readable out-of-process (e.g. `dotnet-counters`) with no exporter configured.
On host shutdown the worker **drains rather than abandons** the queue (everything in it was already
acknowledged to clients): `StopAsync` completes the channel writers, so a late export gets gRPC
`UNAVAILABLE` (retryable, unlike a partial-success rejection), and the loops keep flushing until the
channels are empty or the host's `ShutdownTimeout` (30s default, shared with Kestrel's request
drain) expires — at which point whatever is left is counted on `records_dropped` and logged.

**gRPC services** (`Keryhe.Telemetry.Collector/Services/`): inherit from protobuf-generated base
classes, convert OTLP protobuf messages to Core domain models, delegate to write repositories,
return partial-success responses.

**REST controllers** (`Keryhe.Telemetry.Api/Controllers/`): `Traces`, `Metrics`, `Logs`,
`Alerts`, `Tenants` — each wraps the corresponding read repository with query/aggregation
logic.

**Query layer** (list-pages-server-side plan, phases 1-8): the logs/traces list pages and the
metrics catalog share one shape, all in `Keryhe.Telemetry.Core/Data/Read`. Lists page by
**keyset, not offset** (`KeysetCursor`/`NameKeysetCursor`) — an opaque, filter-hash-checked cursor
encoding `(sort key, tiebreak id)`, never a page number, so a page never shifts under concurrent
inserts. Every summary/page request pins on **`asOf`**: a database-clock value (`ResolveAsOfAsync`,
`DatabaseClockNowExpr`) resolved once per fresh query and echoed back opaquely thereafter — never
parsed or recomputed by the caller — so paging through a window stays stable even as new rows keep
arriving. (There is no "new since" banner or poll: removed by `plans/trace-list-detail-performance.md` Phase 1, so a list refreshes only when the user re-applies the time range.) A summary/count query
that risks running long (an unindexed scan, a `COUNT(*)` over a large filtered set) goes through
**`TimedQuery.RunAsync`**, which enforces `Telemetry:Query:SummaryTimeoutSeconds` (default 5) and
returns a lower-bound/"≥ N" result instead of blocking the request indefinitely — `GetLogSummaryAsync`/
`GetTraceSummaryAsync`/`GetMetricSeriesAsync` (the latter retrying once at a quarter of the
requested point count, decision 31) all use it; with no rollup tables (schema 3.0.0) a 3d/7d summary on
a busy tenant is the case that hits it. `SqlServer`'s own read repositories additionally
wrap each call in `ExecuteWithRetryAsync`, retrying once on error 1205 — every other provider's
override is a no-op. Hot reads filter on `tenant_id`/`service_name` columns carried by `spans`,
`log_records` and `metrics` themselves (copied from the resolved resource at ingest by
`TelemetryIngestionHelpers.TenantAndService`); they join `resources` only where its columns are selected.
Trace/span ids are bound through the `IdParam` hook (a sized ANSI `DbString` on SQL Server, whose id
columns are `varchar` with a binary collation, so a lookup is a seek with no `CONVERT_IMPLICIT`) and
matched in lists through `IdInPredicate` (`= ANY(@ids)` on PostgreSQL/Timescale).

**Deduplication of the reference tables**: three entities are deduplicated, by two different
mechanisms, and the distinction is deliberate.

*Hash-keyed* — Resources and InstrumentationScopes dedup on an unbounded attribute map, so they
carry a SHA-256 hash column (`ResourceHash`, `ScopeHash`) with a UNIQUE constraint.

**A resource hash is not a resource identity.** `HashResource` covers only schema URL + attributes,
so two tenants running the same service with the same attributes produce the same hash — which is
why every schema keys resources on `UNIQUE (tenant_id, resource_hash)`, and why anything holding a
resource identity in memory must go through `TelemetryIngestionHelpers.ResourceKey(tenantId, hash)`.
`ResourceScopeCache.TryGetResource`/`SetResource` take the tenant as a parameter for exactly this
reason. Scopes are the deliberate exception: `UNIQUE (scope_hash)` with no tenant, because a scope is
an instrumentation library and is shared across tenants on purpose.

**`HashResource`/`HashScope` memoize on the model instance** (`ResourceModel.CachedHash` /
`InstrumentationScopeModel.CachedHash`, internal fields, never set outside these two methods). Each
bulk writer hashes the same resource/scope twice per row — once resolving the batch's distinct
resources, again per row rebuilding the lookup key — and the gRPC services hand every record under
one `ResourceLogs`/`ResourceSpans`/`ResourceMetrics` block the *same* `ResourceModel` instance, so
memoizing on the instance turns the second-and-later hash of a batch's dominant resource(s) into a
field read. The cache is populated once and never invalidated within a model's lifetime — treat a
`ResourceModel`/`InstrumentationScopeModel` as immutable once anything has hashed it.

*Natural-keyed* — Metrics dedup on `uk_metric_identity UNIQUE (resource_id, name, type, scope_id)`,
four bounded scalar columns already on the row, so there is no metric hash column. `type` is part
of the key rather than merely refreshed on conflict: the write path picks a data-point table from
the *incoming* type while the read path picks from the *stored* type, so a metric that changed
type and matched an existing row would write points the reader never looks for. Added in schema
2.7.0 — before it, `metrics` grew by one row per export cycle per metric.

Inserts use ON CONFLICT DO UPDATE (Postgres/Timescale), MERGE ... WITH (HOLDLOCK) (SqlServer),
ON DUPLICATE KEY UPDATE ... AS new (MySql) or `ReplacingMergeTree` (ClickHouse). Spans, log records and
the data-point tables are NOT deduplicated (schema 3.0.0): they are plain appends with no unique key and
no foreign keys, so a re-delivered batch is stored again and reads tolerate it (the trace-list row's span
count is `COUNT(DISTINCT span_id)` and trace detail drops a duplicate span id). A metric's `tenant_id` and
`service_name` are written when its catalog row is first inserted only (a cache miss) and cannot go stale:
the resource hash includes `service.name`, so a rename produces a new resource and new `metrics` rows. A singleton
`ResourceScopeCache` (`ConcurrentDictionary`) short-circuits DB lookups for all three, so a warm
process resolves them with no round trip. The cache needs no invalidation because nothing deletes a
resource, scope or metrics catalog row — `IRetentionSettingsRepository` offers retention only. If a delete
that removes catalog rows is ever added, it must clear the cache, or every flush keeps writing rows that
point at a catalog row that no longer exists until the process restarts (the hot tables have no foreign
keys to catch it any more, so it would be silent).

**`metric_last_seen`** (schema 2.13.2, list-pages-server-side plan Phase 5, decision 27): a
separate table, deliberately not a column on `metrics`, that the metrics catalog reads for its
"has data in range" check instead of scanning the five data-point tables. No FK to `metrics` — a
FK would make every touch lock-check the `metrics` row, reintroducing the contention the table
exists to avoid, and nothing deletes `metrics` rows so orphans can't occur. On the four relational
providers it's written by a periodic `MetricTouchWorker`/`MetricTouchTracker` pair (mirroring
`ApiKeyTouchWorker`/`ApiKeyTouchTracker` above, registered unconditionally by
`AddKeryheTelemetryCollector`, bound from its own `Telemetry:MetricTouch` section), draining a
`metric_id → newest time_unix_nano` map each interval into batches sorted by `metric_id` and
capped under 4,000 rows so concurrent collector instances always lock in the same order and never
escalate to a table lock on SQL Server (`DEADLOCK_PRIORITY LOW` plus one retry on error 1205
there). On ClickHouse it's an `AggregatingMergeTree` fed by one materialized view per data-point
table instead — no mutation per flush interval, no worker; `IMetricTouchStore`'s ClickHouse
implementation is a deliberate no-op purely so `MetricTouchWorker` can stay registered
unconditionally on every provider. "Seen in range" is an approximation
(`last_seen_unix_nano >= start AND metrics.created_at <= end`), falling back to an exact
per-candidate `EXISTS` over the five data-point tables when the window's end is more than an hour
in the past.

On Postgres and Timescale, resource/scope/metric-catalog upserts run as their own
auto-committed statements **before** the data transaction opens, rather than inside it —
`ResourceScopeCache` is populated the moment each upsert returns, with no post-commit deferral.
This is load-bearing on Timescale specifically: a data transaction that inserts into a time range
with no existing chunk creates that chunk (and attaches its foreign keys) inline, which takes a
lock on `resources`/`instrumentation_scopes` that conflicts with a concurrent flush's own upsert
lock on those tables — resolving the upserts first means the data transaction never itself holds
that lock. `TelemetryIngestionWorker`'s retry backoff also carries full jitter (random within
[50%, 100%] of the exponential delay) for the same reason: a transient failure like this tends to
hit several concurrent flushes at once, so fixed backoff would retry them all at the same moment.

SqlServer follows the same shape for a related reason: its upserts are `MERGE ... WITH (HOLDLOCK)`
(serializable key-range locks), and held inside the data transaction those ranges lasted through
the bulk copy until commit, deadlocking concurrent flushes, worst on a cold cache. Auto-committed,
they last one statement. Its metrics `MERGE` also updates `description`/`unit` only when they
actually differ, rather than locking the matched row on every cache miss. The SqlServer retention
sweep deletes in 4,000-row chunks (below the lock-escalation threshold) at `DEADLOCK_PRIORITY LOW`.
Spans go in via `INSERT ... WHERE NOT EXISTS` with `FORCESEEK` on `uk_trace_span`, not `MERGE`: the
`MERGE` scanned the clustered key and U-locked other flushes' uncommitted rows, which was the
dominant remaining deadlock under load. A span re-delivered into two concurrent flushes now fails
one of them on `uk_trace_span`; the worker's retry then skips it.

Because `metrics.created_at` now means "first seen" rather than approximately the data timestamp,
metric retention prunes `TelemetryIngestionHelpers.TimePrunedMetricTables` — the five data-point
tables — on `time_unix_nano`, instead of cascading from `metrics`.

**JSONB for attributes**: OpenTelemetry key-value attributes are stored as JSONB/`nvarchar`
columns (`Attributes`, `FilteredAttributes`) rather than normalized tables; Dapper maps them
via `JsonAttributesTypeHandler`.

**Exemplars live on the data point** (schema 2.9.0). OTLP declares `repeated Exemplar exemplars` on
every data point except Summary, so the former single `exemplar_id` column plus shared `exemplars`
table could not represent the signal — and no writer ever populated it. The list is now an
`exemplars_json` column on `gauge_data_points`, `sum_data_points`, `histogram_data_points` and
`exponential_histogram_data_points`, serialized by `MetricService.ConvertExemplars` and read back by
`MetricReadRepositoryBase.DeserializeExemplars`. A child table was rejected because it would need
each data point's generated id, which none of the bulk-load paths (binary `COPY`, `SqlBulkCopy`,
`ClickHouseBulkCopy`) hands back. Trade-off: an exemplar's `trace_id` is no longer indexable; no read
path queries it. Note the .NET SDK emits no exemplars unless a meter provider sets `SetExemplarFilter`
— `Keryhe.Telemetry.TestDataGenerator`'s live sink does, and records each measurement while the span
it belongs to is the current `Activity`, so it carries trace ids; its backfill sink attaches exemplars itself.

**Cumulative histogram/exp-histogram Min/Max are sometimes an estimate, not the true bucket
extreme** (`MetricBucketPoint.MinMaxApproximate`, `MetricReadRepositoryBase.EstimateMax`/
`EstimateMin`). OTLP's cumulative temporality reports Min/Max since the stream started, not since
the previous export, so a bucket's own extreme can only be recovered exactly when it's provably
attributable to that bucket: the counter started/reset there, or the lifetime extreme itself moved
there (a moved extreme can only have moved within the interval since the previous observation).
Otherwise the reported value is a bound, tightened — for `HISTOGRAM` only, via the explicit bucket
that received new observations — to the smaller of the lifetime value and that bucket's own edge;
`EXPONENTIAL_HISTOGRAM` keeps the coarser lifetime-value bound, since recovering a real boundary
value from an (index, scale) pair isn't implemented in this read path (same gap
`FinalizeExpHistogramBounds`'s own doc comment records for exp-histogram bucket bounds generally).
Delta temporality is always exact — the flag only ever applies to cumulative points. The metric
detail page marks the Min/Max stat cards and the percentile chart's Max line with "≈" when any
charted point is approximate.

**Multi-tenant architecture**: `resources`, `api_keys`, `alert_rules`, `spans`, `log_records` and
`metrics` carry a `tenant_id` column (schema 3.0.0), so the hot reads filter on it directly; the five
data-point tables get neither `tenant_id` nor `service_name`, because `metric_id` already identifies
exactly one tenant and service. The ingestion server
resolves tenants by hashing the `Authorization: Bearer <key>` gRPC header against `api_keys`
(`ITenantResolver`). The API takes the tenant from the route (`{base}/tenants/{tenantId}/...`), checks the
caller's access in `TelemetryAuthorizationFilter` and carries it via a scoped `ITenantContext`
(`ApiTenantContext`); read queries filter on `tenant_id`.

**Alerting** (`Keryhe.Telemetry.Api/Alerting`): `AlertService.EvaluateAllAsync` iterates all
tenants with enabled rules, dispatching each rule type to a registered `IAlertEvaluator`
(`MetricThreshold`, `ErrorRate`, `SlowTrace`, `LogSeveritySpike`). `ErrorRateEvaluator` and
`SlowTraceEvaluator` read `ITraceReadRepository.GetTraceSummaryAsync`; `LogSeveritySpikeEvaluator`
reads `ILogReadRepository.GetLogSummaryAsync` — the same summary path the traces/logs list pages use
(see "Trace anchors" below), not a bespoke scan; `SlowTraceEvaluator` therefore counts inbound trace
anchors whose own duration is over the threshold, the same duration the trace list shows. An atomic `TryClaimFireAsync` (UPDATE with cooldown check) prevents duplicate fires under
load balancing. The API's `AlertsController` handles rule CRUD via `IAlertRuleRepository`. Note: no
host currently registers a background worker that drives `EvaluateAllAsync` — evaluation must be
invoked explicitly if you wire it up.

**Retention** (`Keryhe.Telemetry.Api/Retention/`): the single application-level mechanism for
telemetry retention, on every provider. `RetentionWorker`, a `BackgroundService` structurally
mirroring `AlertEvaluationWorker`, wakes on `Retention:IntervalSeconds` (default 3600s, config only —
not part of the DB row), resolves the scoped `IRetentionSettingsRepository`, reads the current windows
via `GetSettingsAsync`, then runs `DeleteOldTracesAsync`/`DeleteOldMetricDataPointsAsync`/
`DeleteOldLogRecordsAsync` against them. Each sweep ends with a "Retention sweep complete" log line that
includes the rows removed and the sweep's elapsed milliseconds. The mechanism is per provider (schema
3.0.0):

| Provider | Retention |
|---|---|
| PostgreSQL, SQL Server, MySQL | Spans and logs: bounded-batch deletes **per tenant** (`tenant_id = @t AND time < @cutoff`) through the `(tenant_id, time)` access path; data points: bounded batches through their time index (PostgreSQL's BRIN, SQL Server/MySQL's `(time_unix_nano)`). The shared loop is `RetentionSettingsRepositoryBase`; a provider supplies only `BatchedDeleteSql` (PostgreSQL has no primary key on these tables, so a batch is `DELETE ... WHERE ctid = ANY (ARRAY(SELECT ctid ... LIMIT n))`; SQL Server `DELETE TOP (n)` at `DEADLOCK_PRIORITY LOW`, batch kept under the lock-escalation threshold; MySQL `DELETE ... LIMIT n`). |
| Timescale | `drop_chunks` per hypertable (`show_chunks` + `approximate_row_count` first, for the returned estimate). Granularity is the chunk interval (6 h spans/logs, 12-24 h data points): a row survives until its whole chunk is older than the cutoff. |
| ClickHouse | `ALTER TABLE ... DROP PARTITION ID` for each fully expired day (spans, `trace_index`, logs, data points); rows counted from `system.parts` first. No row deletes, no mutations. Granularity is the day. |

`AddRetention()` registers the worker; called from `Api.Server` and `Server`'s `Program.cs` only, never
`Collector.Server` — retention is entirely an API-host concern (see `IRetentionSettingsRepository`'s doc
comment for why it replaced the former write-side `ITelemetryWriteStore`). The retention WINDOWS are global
(one settings row), only the relational sweeps iterate tenants. `SettingsController`
(`GET`/`PUT /api/settings/retention`) exposes the same repository for the Angular settings page to edit — no
caching layer, since the worker only reads the row once per sweep interval. The row itself
(`retention_settings`) is a single global singleton (`id = 1`, `CHECK` on relational providers), seeded on
install with today's implicit defaults (traces 90d, logs 90d, metrics 180d); `UpdateSettingsAsync` is always
an `UPDATE`, never an `INSERT`. ClickHouse follows the same "control-plane is best-effort" pattern as its
alert-rule CRUD: `UpdateSettingsAsync` is overridden to use `ALTER TABLE ... UPDATE` instead of the shared
base's plain `UPDATE`.

**Export** (`Keryhe.Telemetry.Api/Controllers/*.cs`'s `GetExport` actions plus
`Keryhe.Telemetry.Api/Export/ExportWriters.cs`, list-pages-server-side plan, Phase 8, decision 17):
`GET /api/logs/export`, `/api/traces/export`, `/api/metrics/export` stream every matching row —
full log records, one `TraceInfo` row per trace, or one `(display series, bucket)` row per metric
series — as NDJSON or CSV, with no row cap, reusing each signal's existing filter compilation so
export can never see a different population than the list it's exporting from. Two limits apply
before any query runs: `ExportWindowGuard` rejects (`400`) a window wider than
`ProviderCapabilities.ExportMaxWindowDays` (7 on PostgreSQL/Timescale/ClickHouse, 1 on SQL Server/MySQL, `Telemetry:Export:MaxWindowDaysOverride`)
regardless of filters — export has no row cap, so the window is the only thing bounding the work —
and `ExportConcurrencyGate`, a singleton `SemaphoreSlim` shared across all three signals, caps
concurrent exports per API instance at `Telemetry:Export:MaxConcurrent` (default 2), returning
`429` rather than queuing. Logs stream via `SqlMapper.ExecuteReaderAsync` +
`SqlMapper.GetRowParser<T>` (Dapper's own `QueryUnbufferedAsync` has no `CommandDefinition`/
cancellation-token overload in the pinned Dapper version) so the reader advances one row at a time
and `CancellationToken` — bound by MVC to `HttpContext.RequestAborted` — reaches the underlying
database command on client disconnect, not just the enumeration loop; `CommandTimeout = 0` is set
explicitly since Dapper's 30s default would cut off a large export. Traces export derives the anchors ONCE and streams them
through the same kind of reader (re-running the anchor query per keyset chunk would repeat the whole window scan for every chunk); every 1,000
anchors a second connection fetches that chunk's exact span counts and whole-trace bounds, so memory stays bounded to one chunk and
`CancellationToken` still reaches the database command. Metrics export reuses `GetMetricSeriesAsync`'s bucketed
pipeline with `Top = int.MaxValue`, so `BuildDisplaySeriesAndOther`'s fold-into-"other" step is
always empty and every series is included (decision 29) — the pipeline already collapses raw data
points down to (streams × buckets) rows in SQL before this ever runs, so there's no separate raw
row set to stream unbuffered. SQL Server runs with `READ_COMMITTED_SNAPSHOT ON` (decision 13), so a
slow-downloading client never holds a shared page lock against ingestion. CSV cells get a
formula-injection guard (`CsvFormulaGuard`): a cell starting with `=`, `+`, `-` or `@` is prefixed
with `'`.

**Trace anchors** (schema-simplification plan, decisions 9-18; replaces the 2.x rollup tables, `RollupWorker`,
`orphan_roots` and root-based anchoring, all of which are gone). Every trace-list read — summary, page,
samples, export — is defined by one **anchor** per trace: the trace's **earliest span in scope** (ranked by
`(start, id)`), where scope is the selected service's own spans when a service is selected (any span kind) and
the whole trace's otherwise. A trace without a root, or whose root arrives late, simply anchors on whatever its
earliest span is at the time of the query. That definition is expressed in three SQL shapes
(`plans/trace-list-detail-performance.md` measured each; the lab benchmark is `TraceQueryBench`, below):
- **`AnchorsSql`, a whole window** (summary, `listTotal`, the `last` page, export, errors mode). Unscoped, and in
  errors mode, it is a hash aggregate joined back to the anchor span (`GROUP BY trace_id` for the earliest start and
  error flag, then the span read by `(trace_id, span_id)`; a tie on start keeps the lowest `id`), which was 2x
  (PostgreSQL, SQL Server) to 5x (MySQL) faster than a window function. With a service selected the narrow
  `(tenant, service, start)` index range makes the `ROW_NUMBER()`/`MAX() OVER (PARTITION BY trace_id)` form
  faster, so that form is used. ClickHouse: `GROUP BY trace_id` with `argMin`. In errors mode (`errorsOnly`) the
  group is restricted to traces with an ERROR span in scope found through the errors index
  (`trace_id IN (SELECT ... status_code = 'ERROR')`), so errors mode never ranks the traces that cannot qualify
  (about 100x faster).
- **`SeekAnchorsSql`, a range of start times** (the trace-list page, slow mode, the slowest-traces samples; every
  relational provider, `SupportsSeekAnchors`). A span is its trace's anchor when `NOT EXISTS` an earlier in-scope span
  of the same trace at or after the look-back start, checked by one `(trace_id, span_id)` seek per candidate. The
  cost follows the candidates, not the window, so a page reads **slices**: `FetchSlicedAnchorPageAsync` starts at
  `Telemetry:Query:PageSliceSeconds` (default 2) of trace start times from the page's edge (the newest for first/next,
  the cursor forward for prev), widens by `PageSliceGrowth` (default 4) until it has `size + 1` anchors, and takes the
  whole remainder once the next slice would cover half of it (bounding a rare search or operation filter). Slices are
  disjoint, so rows concatenate in the final order, and every filter and the keyset predicate apply to each slice
  unchanged. Slow mode filters on the anchor's own duration (a few percent of spans), so it reads the whole range in one
  pass. The anchor's error flag is not computed per candidate; the page reads it for its own rows in the follow-up query
  (`ErrorScope`: service, look-back range, pin). A first page went from 190-770 ms (PostgreSQL) and 574-3,800 ms (MySQL)
  to 4-15 ms. The `last` page still ranks the whole window (it needs the exact count).
  `SlicedPaging_MatchesTheAnchorDefinition_...` checks every nav, filter, page size and slice width against the
  definition restated in LINQ over the seeded spans, and was negative-controlled.
- ClickHouse has no `(trace_id)` seek, so it reads whole-window anchors for pages too (`SupportsSeekAnchors` false).
- **Duration is the anchor span's own** `end - start` everywhere — the row, the `mode=slow` filter, the summary
  percentiles and latency heatmap. The row's service, operation, kind and `DisplaySpanIdHex` are the anchor's;
  the error flag is "any span in scope has ERROR" (so `mode=errors` is that flag); the span count is exact,
  `COUNT(DISTINCT span_id)` over the page's traces in a follow-up query, scoped to the selected service when
  one is selected. The follow-up also supplies the WHOLE trace's start/end for the detail link. The
  request-count card and the summary charts count only inbound anchors (kind `SERVER`/`CONSUMER`); the list
  itself shows every kind and displays it.
- **Operation filter = the anchor's own name** (what the row shows), not "any span in the trace" as in 2.x.
  **Search matches any span in the whole trace regardless of the service filter** ("traces containing X"), as an
  `EXISTS` through `(trace_id, span_id)` per candidate anchor within the search window; the row stays anchored
  per the rule above.
- **Look-back margin**: the derivation scans `[start - lookback, end]` (`Telemetry:Query:AnchorLookbackMinutes`,
  default 5) and drops anchors that start before the window, so a trace that began inside the margin before the
  window is excluded from it rather than anchored on a later span. A trace that began before the margin is
  anchored on its earliest span inside it (accepted).
- **`asOf` pins the span set before ranking** (`created_at <= @asOf` inside the derived table), so a root that
  arrives after the pin is not the anchor within a pinned query and becomes the anchor on the next fresh one. The
  summary is pinned like the page (3.0.1): the chart, the cards, `listTotal` and the list describe the same traces,
  from ONE pass over the anchors (the inbound rows, with `listTotal` carried by a window count taken before the
  inbound filter; a separate count only when no inbound anchor came back). The chart therefore leaves out the last
  `AsOfBackoffSeconds` (5 s on PostgreSQL/Timescale, 0 elsewhere). There is no "new since" banner or poll on the trace
  list or the logs page (removed in the same plan): a list refreshes when the user re-applies the time range.
- `Source` on the summary responses is always `"raw"` (the field stays so the client contract is unchanged).

**Trace detail** (`GET /api/traces/{traceId}/spans?start=`, `TraceDetailResponse`): the response lists each distinct
resource and instrumentation scope once and the spans refer to them by `resourceIndex`/`scopeIndex` (a span used to
carry its own copy of its resource attributes, so a trace of thousands of spans repeated a few large attribute sets
thousands of times: 3.9x smaller on a 31-span test trace with large resources). The repository hands every span of a resource the same instance, parsing each distinct resource's and scope's attribute
JSON once. On the relational providers the span query keeps its single plain join to the reference tables (one round
trip, one trace probe); three alternatives were measured and rejected: three round trips (+40-60% per detail), one
batched statement with the reference selects as `IN (SELECT ... FROM spans WHERE <trace>)` sub-selects (the trace
predicate runs three times: a hypertable probes every chunk three times, and SQL Server's plan degraded under
concurrency, 32 -> 180 ms p95 in the stress run), and `ROW_NUMBER()` to send each resource's attributes once (the
windows sort every row: a 20,000-span trace 551 ms on SQL Server). ClickHouse reads the reference rows in their own
queries (`JoinsReferenceRows` false; `ReferenceRowsSql` filters the raw table before `LIMIT 1 BY id`). The optional `start` and `end` (used
together) are the trace's extent, which the list rows and dashboard widgets already carry (`traceStartTime`/`traceEndTime`)
and pass in the link (`?start=&end=`; a log's timestamp is NOT such a hint, so log links do not pass one). Timescale and
ClickHouse use them (`HintedTraceTimeBounds`): `[start - margin, end + margin]` (`Telemetry:Query:TraceHintMarginMinutes`,
default 1; `Telemetry:Query:TraceHintEnabled=false` ignores every hint) replaces a probe of every chunk (Timescale) or the `trace_index` round trip (ClickHouse); PostgreSQL, SQL Server and
MySQL seek `(trace_id, span_id)` and ignore it. The range is the trace's own extent on purpose: a first version bounded only
the start (`[start - 5 min, start + 24 h]`), and under live ingestion the stress run showed Timescale detail 16 -> 29 ms (a wide
range on the chunk being written gives the planner a time-index path it misjudges from stale statistics). A hint that finds
nothing falls back to the unbounded read (a wrong hint is never a 404). A span that arrived after the list was read and lies
beyond the margin is missing from a hinted read, which is the limit of a hint; an unhinted read is always whole.

### Database

Providers: plain PostgreSQL, PostgreSQL + TimescaleDB, SQL Server 2022, ClickHouse, or MySQL 8.0.19+. Schema
3.0.0 (fresh install only — `plans/schema-simplification.md`):

- **Hot tables are append targets.** `spans`, `log_records` and the five data-point tables have no unique
  key (a re-delivered span is stored twice, decision 7) and no foreign keys to `resources`, scopes or
  `metrics` (reference rows are committed before the data transaction, so a hot-table FK only cost insert
  time — ~7-8% of PostgreSQL flush time — and made Timescale `drop_chunks` deadlock). FKs stay on
  `api_keys`, `alert_rules`, `alert_events` and `metrics`. `id` is an identity/auto-increment used only as
  the keyset tiebreak and has no index of its own on PostgreSQL/Timescale.
- **`tenant_id` and `service_name` columns** on `spans`, `log_records` and `metrics`; the five data-point
  tables get neither (`metric_id` identifies one tenant and service).
- **Id columns match their driver's parameter type**: `text` on PostgreSQL/Timescale; `varchar(32)`/
  `varchar(16)` `COLLATE Latin1_General_BIN2` on SQL Server (sent as sized `AnsiString`); `char(32)`/`char(16)`
  `ascii_bin` on MySQL; `String` on ClickHouse.
- **Index set (relational), every index serving a named query**: `spans (trace_id, span_id)` (trace detail, span
  by id, spans by parent, service-map parent join), `(tenant_id, start_time_unix_nano)` (unscoped anchors,
  windows, search, service-map child range, per-tenant retention), `(tenant_id, service_name, start_time_unix_nano)`
  (service-scoped anchors) and an errors index (`WHERE status_code = 'ERROR'` partial/filtered on PostgreSQL,
  Timescale and SQL Server; MySQL has no filtered index, so `(tenant_id, status_code, start_time_unix_nano)`,
  which Phase 4 should measure and drop if its insert cost is material); `log_records (tenant_id,
  time_unix_nano, id)` and `(trace_id)`; `metrics` unique `(resource_id, name, type, scope_id)` and
  `(tenant_id, service_name, name)`; data points `(metric_id, time_unix_nano, id)` plus a time index for
  retention (PostgreSQL BRIN; SQL Server/MySQL `(time_unix_nano)`; none on Timescale/ClickHouse);
  `resources` unique `(tenant_id, resource_hash)` and `(tenant_id, service_name)`;
  `instrumentation_scopes` unique `(scope_hash)`. No GIN/trigram/skip indexes, no `pg_trgm`.
- **SQL Server** clusters on the access path, not an IDENTITY: `spans (tenant_id, start_time_unix_nano, id)`,
  `log_records (tenant_id, time_unix_nano, id)`, data points `(metric_id, time_unix_nano, id)` (unique clustered
  indexes, so `id` only makes the key unique), so concurrent flushes append at one tail per tenant instead of
  contending for one last page. `ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON` — the opt-in SNAPSHOT
  isolation plumbing and its `ALLOW_SNAPSHOT_ISOLATION` are gone; reads still run at `DEADLOCK_PRIORITY LOW` on a
  distinct Application Name. Spans are written by `SqlBulkCopy` straight into the table (no `#spans_stage`).
- **MySQL** InnoDB clusters on the primary key, so `spans` is `PRIMARY KEY (tenant_id, start_time_unix_nano, id)`,
  `log_records (tenant_id, time_unix_nano, id)`, data points `(metric_id, time_unix_nano, id)`, each with
  `KEY (id)` (InnoDB needs an auto-increment column to lead some index). Span rows are sorted by
  `(trace_id, span_id)` and log rows by time before the multi-row insert: unordered inserts deadlock on
  secondary indexes.
- **PostgreSQL** is plain heap tables (day partitioning can be added later without re-keying). **Timescale**
  has hypertables on `spans`, `log_records` and the five data-point tables (6 h chunks for spans/logs, 12-24 h
  for data points; `create_default_indexes => FALSE`), compression after 7 days (`segmentby` `tenant_id` for
  spans/logs, `metric_id` for data points), `set_integer_now_func` registered, retention by `drop_chunks`,
  no continuous aggregates. **ClickHouse** is described in its provider notes above.
- **Removed from 2.x**: the views (`trace_summary`, `service_map`, `service_map_detailed`,
  `log_severity_stats`) and Timescale's `log_severity_stats_daily` continuous aggregate (nothing referenced
  them), the rollup tables, `rollup_state`, `orphan_roots`, `uk_trace_span`, the MySQL `is_root` column, and
  `schema/migrations/`.

**Telemetry (10)**: `resources`, `instrumentation_scopes`, `spans` (events and links folded into
its `events_json`/`links_json` columns — there are no separate `span_events`/`span_links` tables), `metrics`, `gauge_data_points`, `sum_data_points`,
`histogram_data_points`, `exponential_histogram_data_points`, `summary_data_points`, `log_records`

**Multi-tenant/auth (2)**: `tenants`, `api_keys`

**Alerting (2)**: `alert_rules`, `alert_events`

**Retention (1)**: `retention_settings`

**Utility (1)**: `schema_version`

There are no views and no derived-data tables; ClickHouse additionally has `trace_index` and `metric_last_seen`
(both fed by materialized views).

Connection strings (both hosts point at the same database):
- Ingestion server reads `ConnectionStrings:Collector` in `Keryhe.Telemetry.Collector.Server/appsettings.json`
- API server reads `ConnectionStrings:Api` in `Keryhe.Telemetry.Api.Server/appsettings.json`
- The all-in-one `Keryhe.Telemetry.Server` reads **both**, and requires them to be identical under
  the Npgsql-backed providers (see the all-in-one constraint above)
- Both select the provider via the `Database:Provider` key in the same file

### Proto Files

OpenTelemetry proto files live under `src/Keryhe.Telemetry.Collector/opentelemetry/` and are
compiled to C# gRPC stubs automatically via MSBuild (`Grpc.Tools`). Covers traces, metrics,
logs, profiles, resources, and common types.
