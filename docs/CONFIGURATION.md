# Configuration reference

Every configuration key the hosts and libraries read, with its default. Anything a host's `appsettings.json` does not
set falls back to the default shown here. Any key can be set the usual .NET ways: `appsettings.json`,
`appsettings.{Environment}.json`, User Secrets, environment variables (`:` becomes `__`, e.g.
`Telemetry__Query__SummaryTimeoutSeconds=10`) or the command line (`--Telemetry:Query:SummaryTimeoutSeconds=10`).

**Which host reads what.** The collector host (`Keryhe.Telemetry.Collector.Server`) reads the write-side sections, the
API host (`Keryhe.Telemetry.Api.Server`) the read-side and UI sections, and the all-in-one host
(`Keryhe.Telemetry.Server`) both. The "Host" column says which.

## Database and connection strings

| Key | Default | Host | Description |
|---|---|---|---|
| `Database:Provider` | none (required) | all, Admin | `PostgreSQL`, `Timescale`, `SqlServer`, `ClickHouse` or `MySql`. Unknown or missing fails startup. |
| `ConnectionStrings:Collector` | none (required) | collector, all-in-one | Write path: bulk writer, tenant resolution, API-key and metric touch workers. |
| `ConnectionStrings:Api` | none (required) | API, all-in-one | Read path: every read repository, alert rules, retention settings. |
| `ConnectionStrings:Admin` | none (required) | Admin tool | `Keryhe.Telemetry.Admin`'s connection. |

On PostgreSQL and Timescale the all-in-one host requires `Api` and `Collector` to be identical (it fails at startup
otherwise; see CLAUDE.md, "All-in-one constraint"). The committed `appsettings.json` files leave the connection strings
empty; local values live in User Secrets.

## Ingestion (`Telemetry:Ingestion`)

Collector and all-in-one hosts. `TelemetryIngestionOptions`.

| Key | Default | Description |
|---|---|---|
| `MaxQueuedLogRecords` | 200000 | Log records resident in the ingestion queue before a further export waits (backpressure). |
| `MaxQueuedMetrics` | 200000 | The same, for metrics. |
| `MaxQueuedSpans` | 200000 | The same, for spans (counted in spans, not traces). |
| `MaxLogFlushBatchSize` | 2000 | Log records merged into one flush. |
| `MaxMetricFlushBatchSize` | 2000 | Metrics merged into one flush. |
| `MaxTraceFlushSpanBatchSize` | 2000 | Spans merged into one trace flush. |
| `FlushConcurrency` | 4 | Concurrent flush loops per signal. |
| `MaxFlushRetries` | 5 | Retries after a failed flush before the batch is dropped (counted on `records_dropped`). |
| `RetryBaseDelayMilliseconds` | 200 | First retry delay; doubles each attempt, with jitter. |
| `RetryMaxDelayMilliseconds` | 5000 | Cap on the retry delay. |

## Tenant resolution (`Telemetry:TenantResolution`)

Collector and all-in-one hosts. `TenantResolutionOptions`.

| Key | Default | Description |
|---|---|---|
| `PositiveCacheTtlSeconds` | 30 | How long a valid API key's tenant is cached. |
| `NegativeCacheTtlSeconds` | 5 | How long an invalid or inactive key is cached. |
| `LastUsedFlushIntervalSeconds` | 60 | Interval of the `api_keys.last_used_at` flush. |

## Metric touch (`Telemetry:MetricTouch`)

Collector and all-in-one hosts. `MetricTouchOptions`. Writes `metric_last_seen` on the relational providers (a no-op on
ClickHouse, where materialized views do it).

| Key | Default | Description |
|---|---|---|
| `FlushIntervalSeconds` | 60 | Interval between flushes. |
| `MaxBatchSize` | 4000 | Rows per batch (kept under SQL Server's lock-escalation threshold). |

## Read queries (`Telemetry:Query`)

API and all-in-one hosts. `QueryOptions`.

| Key | Default | Description |
|---|---|---|
| `SummaryTimeoutSeconds` | 5 | Budget for a summary query (trace and log summaries, metric series and exemplars, exact counts). Past it the cards and charts report "timed out" and the total falls back to a capped count, which gets its own budget of the same length, so a timed-out request takes about twice this. |
| `RawSearchWindowHoursOverride` | unset (24) | Longest window a free-text/attribute search or `mode=slow` may cover; wider is a `400`. |
| `AnchorLookbackMinutes` | 5 | How far before a trace-list window the anchor derivation looks, so a trace that began just before the window is not listed on a later span. |
| `PageSliceSeconds` | 2 | First slice of trace start times a trace-list page scans (relational providers). |
| `PageSliceGrowth` | 4 | Factor each further slice widens by (minimum 2). |
| `TraceHintMarginMinutes` | 1 | Margin either side of a trace-detail `?start=&end=` hint (Timescale, ClickHouse). |
| `TraceHintEnabled` | true | `false` ignores every trace-detail time hint. |

## Export (`Telemetry:Export`)

API and all-in-one hosts. `ExportOptions`.

| Key | Default | Description |
|---|---|---|
| `MaxWindowDaysOverride` | unset (7 on PostgreSQL/Timescale/ClickHouse, 1 on SQL Server/MySQL) | Widest window an export may cover; wider is a `400`. |
| `MaxConcurrent` | 2 | Exports streaming at once per API instance; beyond it a request gets `429`. |

## API (`Telemetry:Api`)

API and all-in-one hosts. `TelemetryApiOptions`; full detail in [src/Keryhe.Telemetry.Api/README.md](../src/Keryhe.Telemetry.Api/README.md).

| Key | Default | Description |
|---|---|---|
| `BasePath` | `/api` | Prefix of every API route. `/` and empty are rejected. The in-repo hosts set `/telemetry/api`. |
| `Authorization:Enabled` | false | Turns on operation and tenant authorization. |
| `Authorization:Policies:{Read\|Admin\|Export\|ManageAlerts\|ManageSettings}` | unset | Name of a host-registered policy per operation. Export falls back to Read, ManageAlerts/ManageSettings to Admin, Read/Admin to the default policy. |
| `Authorization:TenantMappings` | `[]` | List of `{ ClaimType, ClaimValue, Tenants }`: callers with that claim may access those tenants (names, ids or `"*"`). |

## Alerting (`AlertEvaluation`)

API and all-in-one hosts (`AddAlerting`). `AlertingOptions`.

| Key | Default | Description |
|---|---|---|
| `Enabled` | true | When false the evaluation worker registers but never runs. |
| `IntervalSeconds` | 60 | Interval between evaluation cycles. |

## Retention (`Retention`)

API and all-in-one hosts (`AddRetention`). `RetentionOptions`. How many days to keep is not configuration: it is the
`retention_settings` row, edited on the Settings page.

| Key | Default | Description |
|---|---|---|
| `Enabled` | true | When false the retention worker registers but never sweeps. |
| `IntervalSeconds` | 3600 | Interval between sweeps. |

## UI (`TelemetryUi`)

API and all-in-one hosts (`AddKeryheTelemetryUi`). `TelemetryUiOptions`; full detail in
[src/Keryhe.Telemetry.Ui/README.md](../src/Keryhe.Telemetry.Ui/README.md). Read once at startup.

| Key | Default | Description |
|---|---|---|
| `BasePath` | `/` | Where the UI is mounted. A non-root value makes `GET /` a 404; a reverse proxy must forward the prefix. The in-repo hosts set `/telemetry`. |
| `ApiBasePath` | `Telemetry:Api:BasePath`, else `/api` | Where the browser finds the API; may be an absolute URL on another origin. |
| `BrandName` | `Sentinel` | Product name in the header and browser tab. |
| `BrandTagline` | `OpenTelemetry Visualization` | Tagline under the brand name. |
| `HealthThresholds:ErrorRate:Warn` | unset (0.01 in the SPA) | Dashboard Error Rate warning threshold, a 0-1 fraction. |
| `HealthThresholds:ErrorRate:Critical` | unset (0.05 in the SPA) | Error threshold; must be above `Warn`. |
| `Auth:Mode` | `cookie` | `cookie` (the host owns sign-in) or `oidc` (the SPA signs in itself and sends a bearer token). |
| `Auth:LoginUrl` | unset | Where the SPA sends the browser on a `401` (cookie mode). |
| `Auth:LogoutUrl` | unset | Sign-out link (cookie mode). |
| `Auth:IncludeCredentials` | false | Send cookies on cross-origin API calls. |
| `Auth:Oidc:Authority` | unset | OIDC issuer (oidc mode). |
| `Auth:Oidc:ClientId` | unset | OIDC client id (oidc mode). |
| `Auth:Oidc:Scope` | `openid profile` | Requested scopes (oidc mode). |

## Host settings

| Key | Default | Host | Description |
|---|---|---|---|
| `Cors:AllowedOrigins` | `["http://localhost:4201"]` | API, all-in-one | Origins allowed to call the API from a browser. Only matters for a UI on a different origin from its API. |
| `Kestrel:Endpoints` | per host | all | Listening ports and protocols (CLAUDE.md, "Default ports"). Don't set `ASPNETCORE_URLS` on the all-in-one host: it replaces these endpoints and breaks h2c gRPC on 5117. |
| `Logging:LogLevel` | ASP.NET Core defaults | all | Standard .NET logging levels. |
| `AllowedHosts` | `*` | all | Standard ASP.NET Core host filtering. |

## Test data generator (`Generator`)

`Keryhe.Telemetry.TestDataGenerator` only (`GeneratorOptions`); its [appsettings.json](../src/Keryhe.Telemetry.TestDataGenerator/appsettings.json)
sets every value and is the working example. API keys go in User Secrets, never appsettings.

| Key | Default | Description |
|---|---|---|
| `OtlpEndpoint` | `http://localhost:5117` | Collector to send to. |
| `Seed` | 42 | Seed for every random decision; the same seed and time range give the same telemetry. |
| `PeakRequestsPerSecond` | 2 | Traffic at the daily peak, before each tenant's `Scale`. |
| `Environment` | `production` | `deployment.environment` resource attribute. |
| `PodsPerService` | 2 | Simulated pods (resources) per service. |
| `LiveMetricIntervalSeconds` | 15 | Metric export interval when live. |
| `LiveLagSeconds` | 10 | How far behind the clock live emission runs, so requests have finished. |
| `Live` | true | Keep emitting live after any backfill. |
| `Backfill:Enabled` | false | Send history first (hand-built OTLP). Re-running over the same window stores it twice. |
| `Backfill:Window` | `1.00:00:00` (24 h) | How much history. |
| `Backfill:ChunkSeconds` | 60 | Simulated time per chunk (one metric interval). |
| `Backfill:MaxSpansPerExport` | 2000 | Spans per OTLP export during backfill. |
| `Tenants` | `[]` | List of `{ Name, ApiKey, Scale }`; `ApiKey` must already exist in the database. |
| `Incidents` | `[]` | Recurring daily windows: `{ Name, Tenant, Service, Route, At, Duration, LatencyMultiplier, ExtraErrorRate, Version }`. |
