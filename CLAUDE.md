# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

User-facing docs live in `docs/`: [SETUP](docs/SETUP.md), [CONFIGURATION](docs/CONFIGURATION.md) (every key and default),
[ARCHITECTURE](docs/ARCHITECTURE.md), [COLLECTOR](docs/COLLECTOR.md), [API](docs/API.md), [UI](docs/UI.md),
[PARQUET](docs/PARQUET.md), [CLICKHOUSE](docs/CLICKHOUSE.md), [SQLITE](docs/SQLITE.md), [PROVIDERS](docs/PROVIDERS.md),
[STRESS-TESTS](docs/STRESS-TESTS.md). This file holds what a code change can trip over; read the matching doc for behaviour.

## Commands

Requires the .NET 10 SDK and Node.js (Angular 20 for the UI).

```bash
dotnet build Telemetry.sln

# The two hosts, each in its own terminal (they share only the databases)
dotnet run --project src/Keryhe.Telemetry.Collector.Server   # OTLP; Development also h2c http://localhost:5117
dotnet run --project src/Keryhe.Telemetry.Api.Server         # REST API + bundled UI at http://localhost:5188/telemetry

# Tenants and API keys (needs ConnectionStrings:Admin in its user secrets; for Sqlite the API host must have created the file)
dotnet run --project src/Keryhe.Telemetry.Admin

# UI dev server (http://localhost:4201, proxies /api to https://localhost:7105): run the API with --Telemetry:Api:BasePath=/api
cd src/telemetry-client && npm install && npm start

# Publish (the API host also builds the Angular UI through Keryhe.Telemetry.Ui; -p:BuildSpa=false uses an existing dist)
dotnet publish src/Keryhe.Telemetry.Collector.Server -c Release -o ./publish-collector
dotnet publish src/Keryhe.Telemetry.Api.Server -c Release -o ./publish

# Schema: two components, one script per provider; the runner skips a component whose target version is recorded
schema/apply-schema.sh <controlplane|telemetry> <postgresql|sqlserver|mysql|clickhouse|sqlite|parquet> [database]

# Test data generator (tenant keys go in user secrets as Generator:Tenants:N:ApiKey). Backfill is opt-in; backfilling the
# same window twice stores it twice (truncate the hot tables first, never the reference tables)
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator
dotnet run --project src/Keryhe.Telemetry.TestDataGenerator -- --Generator:Backfill:Enabled=true [--Generator:Backfill:Window=01:00:00 --Generator:Live=false]
```

### Tests

```bash
dotnet test tests/Keryhe.Telemetry.IntegrationTests                                     # every provider (Docker)
dotnet test tests/Keryhe.Telemetry.IntegrationTests --filter Provider=SqlServer        # PostgreSQL|SqlServer|MySql|ClickHouse|Parquet|Sqlite
dotnet test tests/Keryhe.Telemetry.IntegrationTests --filter "Provider=Parquet|Provider=Sqlite"   # no Docker needed
dotnet test tests/Keryhe.Telemetry.IntegrationTests --filter Suite=ApiHttp             # also Suite=CollectorAuth; in-process, no Docker
dotnet test tests/Keryhe.Telemetry.IntegrationTests --filter "FullyQualifiedName~SlicedList"   # a single test
dotnet test tests/Keryhe.Telemetry.Parquet.Tests                                       # Parquet unit suite, no Docker
dotnet test tests/Keryhe.Telemetry.TestDataGenerator.Tests                             # run after touching a generator sink
```

- Integration tests run the real `Add<Provider>…Services` registrations against Testcontainers databases with the real `schema/*.sql`,
  seeded through `ITelemetryBulkWriter` (not the generator, whose data is time-dependent). Container start, schema and tenant/key
  seeding live in `tests/Keryhe.Telemetry.TestInfrastructure` (one `ProviderContainer` per provider, `TenantSeeder`), shared with the
  stress harness. ClickHouse's container composes a PostgreSQL control plane; Parquet's control plane is a SQLite file
  (`ProviderContainer.UseSqliteControlPlane(dir)` moves any provider's there).
- Behaviour shared by all providers is a `*TestsBase` with one subclass per provider (retention, control plane, trace anchors, rollups,
  timeouts, export…). Several tests are negative-controlled; keep that when changing them.
- The Angular project has `npm test` (Karma) but no meaningful tests.
- **Opt-in lab benchmarks** (no-ops unless their env var is set): `TraceQueryBench` (`TRACE_BENCH_OUT=<file>`, `TRACE_BENCH_SQL=<file>` for raw
  SQL variants) and `RollupQueryBench` (`ROLLUP_BENCH_OUT`). Run **one provider at a time** (xUnit runs provider collections in parallel):
  `TRACE_BENCH_OUT=/tmp/b.txt dotnet test tests/Keryhe.Telemetry.IntegrationTests --no-build --filter "Provider=PostgreSQL&FullyQualifiedName~TraceQueryBench"`.
- **Stress harness** (`tests/Keryhe.Telemetry.StressTests`, manual, never in `dotnet test`): `dotnet run --project tests/Keryhe.Telemetry.StressTests -- run --provider <p|all> --profile <smoke|ramp|ramp-write-only|…|file.json>`;
  `report --in stress-results/<run>` rebuilds a report. Details in [docs/STRESS-TESTS.md](docs/STRESS-TESTS.md). It reuses the
  Collector's generated gRPC client stubs, which is why the three `*_service.proto` entries are `GrpcServices="Both"`. The write path it
  measures is `IngestionMetrics` (`commit_lag` is the read-independent health signal).

## Schema versioning

Two components, `control-plane` and `telemetry`, each at 4.0.1 with its own version table. A change bumps only its component's third digit,
in one commit:
- **Control plane:** the four `*-ControlPlane.sql` scripts (PostgreSQL, SQL Server, MySQL, SQLite), `CONTROLPLANE_TARGET_VERSION` in
  `apply-schema.sh`, `SqliteControlPlaneSchema.TargetVersion` and the Admin tool's `SqliteSchemaGate.TargetVersion`, **plus** a SQLite migration
  `schema/SQLite-ControlPlane-Migrations/<from>-to-<to>.sql` (plain DDL/DML, no transaction/pragma/version insert) and a copy of the previous
  create script in `tests/Keryhe.Telemetry.IntegrationTests/SqliteSchemas/<from>.sql`.
- **Telemetry:** the four `*-Telemetry.sql` scripts and `TELEMETRY_TARGET_VERSION`.

Otherwise there are no migrations: the scripts create a fresh database. No foreign key or query crosses the two components
(`resources.tenant_id` is a plain column). `ColumnLimitsTests` fails when a sized client-text column and `ColumnLimits` disagree.

## Architecture

OTLP ingestion and visualization: a **collector host** receives OTLP (gRPC and HTTP), a **REST API host** serves queries and the Angular UI.
They always run as **two separate processes** sharing only the databases (different auth, transport guards and Kestrel protocols; the
PostgreSQL provider registers a process-wide `NpgsqlDataSource` per side that two registrations would overwrite).

```
SDKs → Collector (OTLP services → OtlpIngestors → write repos → TelemetryIngestionChannel, gated)
     → TelemetryIngestionWorker (or ClickHouse/Parquet's own partitioned worker) → ITelemetryBulkWriter → telemetry DB
       (auth: bearer key → ITenantResolver → IApiKeyLookup → control-plane DB)
UI → API controllers → I*ReadRepository (active provider, Dapper) → telemetry DB; alert rules, tenants, retention → control-plane DB
```

- **Composition roots.** Both hosts are thin `Program.cs` shells. `AddKeryheTelemetryCollector`/`MapKeryheTelemetryCollector`
  (`Keryhe.Telemetry.Collector/TelemetryCollectorExtensions.cs`) and `AddKeryheTelemetryApi`/`MapKeryheTelemetryApi` (`Keryhe.Telemetry.Api`)
  register no database provider: each host `switch`es on config and calls the provider's own methods, so neither library references a
  provider project.
- **Two providers, both required, on both hosts.** `Database:Provider` (`PostgreSQL`, `SqlServer`, `MySql`, `ClickHouse`, `Parquet`) →
  `Add<P>CollectorServices`/`Add<P>ApiServices`. `ControlPlane:Provider` (`PostgreSQL`, `SqlServer`, `MySql`, `Sqlite`) →
  `Add<P>ControlPlaneCollectorServices`/`Add<P>ControlPlaneApiServices` (SQLite: `AddSqliteApiKeyLookup`/`AddSqliteControlPlaneManagement`),
  against `ConnectionStrings:ControlPlane`. They are a pair because the alert-rule repositories need `ITenantContext`, which only the API
  registers. Unknown/missing values throw at startup naming the key; there is no fallback between connection strings.
- **Data access is Dapper throughout** (no EF Core). Provider-agnostic pieces (write repos, channel, worker, caches, read-repository bases,
  `TimedQuery`, `MetricSeriesPipeline`, rollup builder) are in `Keryhe.Telemetry.Core/Data`; there is no separate Data project.
- **PostgreSQL pools:** an unkeyed `NpgsqlDataSource` for telemetry and a keyed one (`PostgreSqlControlPlane.ServiceKey`) for the control
  plane, taken via `[FromKeyedServices]`, so PostgreSQL can be both or only the control plane.
- **Proto files** under `src/Keryhe.Telemetry.Collector/opentelemetry/` compile to stubs via `Grpc.Tools`.

### Write path

- **Channel and gates.** `TelemetryIngestionChannel` has one unbounded channel per signal; backpressure is a `RecordCountGate` per signal
  bounding resident **records** (spans for traces) and **bytes** (protobuf size), per tenant as well (`maxShare`, `TenantRateLimiter`).
  A full gate refuses after `MaxGateWaitMilliseconds` with `IngestionRejectedException` → gRPC `UNAVAILABLE` + `RetryInfo` (HTTP 503).
- **The traces channel carries flat `List<SpanModel>`**: `TraceWriteRepository` resolves each span's effective resource/scope once, so bulk
  writers never regroup or re-apply fallbacks.
- **Worker.** `FlushConcurrency` drain loops per signal; the in-memory drain step is serialized per signal (interleaving corrupts the gate
  accounting). Flush retries with jittered backoff; `IFlushErrorClassifier` marks permanent errors and `BatchBisector` splits down to the bad
  records. Gates are released for everything drained regardless of outcome; an eventual drop is counted on `records_dropped{reason}` and is
  **not** reflected in the export's partial-success response. On shutdown the worker drains (late exports get `UNAVAILABLE`).
- **Enqueue stamping.** `TelemetryIngestionChannel.MarkEnqueued`/`TryTakeEnqueued` is a weak side table keyed by the export's list, carrying the
  enqueue time and bytes to the flush (`commit_lag`, byte release).
- **Attributes and limits.** `OtlpAttributeConverter` is the only place OTLP values are converted and `Telemetry:Ingestion:Limits` applied;
  sized columns are clipped to `ColumnLimits` (resource `service.name` before hashing).
- **Collector auth** lives in the pipeline: `ApiKeyAuthenticationHandler` acts only on endpoints with `CollectorEndpointMetadata`, hashes the
  bearer key (SHA-256 lowercase hex; the Admin tool's `ApiKeyHashing` and the test `ApiKeyHasher` are deliberate byte-identical copies) and
  writes a trailers-only gRPC response on refusal. `CachingTenantResolver` caches positive 30 s / negative 5 s and checks `expires_at` on every
  call; a throwing lookup is `Unavailable` (retryable), never cached. The `Export` methods must keep `catch (RpcException) { throw; }` ahead of
  their generic catch. `PlaintextTransportGuard` fails startup on a plaintext address outside Development. **Never set `ASPNETCORE_URLS` on the
  collector**: it replaces `Kestrel:Endpoints` and breaks h2c on 5117.

### Reference-table dedup and caches (relational providers)

- `resources` are keyed `UNIQUE (tenant_id, resource_hash)`: **a resource hash is not an identity**, so in-memory keys go through
  `TelemetryIngestionHelpers.ResourceKey(tenantId, hash)`. Scopes are `UNIQUE (scope_hash)` with no tenant, on purpose.
- `HashResource`/`HashScope` memoize on the model instance (`CachedHash`): treat a model as immutable once hashed.
- `metrics` dedup on `(resource_id, name, type, scope_id)`; `type` is in the key because write and read pick the points table from it.
- Upserts run auto-committed **before** the data transaction (PostgreSQL `ON CONFLICT`, SQL Server `MERGE … HOLDLOCK`, MySQL
  `… AS new ON DUPLICATE KEY UPDATE`, MySQL 8.0.19+ only). Holding them inside it deadlocks concurrent flushes.
- `ResourceScopeCache` never invalidates because nothing deletes resource, scope or metric catalog rows. **Any delete of catalog rows must
  clear it**, or flushes silently write rows pointing at missing parents (hot tables have no FKs).
- Hot tables (`spans`, `log_records`, five data-point tables) are plain appends: no unique key, no FKs. Reads tolerate duplicates
  (`COUNT(DISTINCT span_id)`, detail drops repeated span ids, baseline lookups use `ToDictionaryNewest`).
- `metric_last_seen` is a separate table (no FK) written by `MetricTouchWorker` in batches sorted by `metric_id`, under 4,000 rows (SQL Server
  lock escalation). `metrics.created_at` means first seen; metric retention deletes points by `time_unix_nano`, never cascading.
- Exemplars are an `exemplars_json` column on the data points (bulk loaders return no generated ids).
- Cumulative histogram Min/Max can be estimates (`MinMaxApproximate`); the UI marks them "≈".

### Read path

- **Capped lists, no paging.** Logs/traces lists, metrics catalog and exemplars return at most `limit` (`Telemetry:Query:Limits`) as
  `{items, truncated}` by fetching `limit + 1`. Logs and traces take `order=newest|oldest`. No cursors.
- **`TimedQuery.RunAsync`** bounds summary-class reads (`SummaryTimeoutSeconds`) and returns a lower bound. A timeout is decided by **its own
  deadline, not the exception type** (each driver throws its own type), the driver gets budget + `DriverTimeoutGraceSeconds`, and **a
  connection a timed-out query ran on is never reused**. Timeouts count on `query_timeouts`.
- **Search** is unindexed and window-bounded on every provider (`RawSearchWindowGuard`, 24 h); `ProviderCapabilities` and
  `GET /api/capabilities` report the limits the UI uses.
- Ids bind through `IdParam` (sized ANSI `DbString` on SQL Server, binary-collation `varchar`, no `CONVERT_IMPLICIT`) and lists through
  `IdInPredicate`. SQL Server reads retry once on deadlock 1205 (`ExecuteWithRetryAsync`) and run at `DEADLOCK_PRIORITY LOW` with RCSI on.
- **Trace anchors.** Every trace-list read (list, samples, export) has one anchor per trace: its **earliest span in scope** by `(start, id)`,
  scope = the selected service's spans, else the whole trace. Duration, service, operation and kind are the anchor's; the error flag is "any
  span in scope"; the operation filter matches the anchor's name; search matches any span of the trace regardless of service. The scan covers
  `[start - AnchorLookbackMinutes, end]` and drops anchors before `start`. Three SQL shapes: `AnchorsSql` (whole window, hash aggregate; window
  function when a service is selected; errors mode restricted through the errors index), `SeekAnchorsSql` + `FetchSlicedAnchorPageAsync`
  (relational lists read widening start-time slices with a `NOT EXISTS` seek per candidate), and ClickHouse/Parquet's own slices confirmed
  against `trace_index` (Parquet confirms in list order and stops once the page is full). `SlicedList_MatchesTheAnchorDefinition` restates the
  definition in LINQ: keep it passing.
- **Trace detail** returns each distinct resource/scope once with spans referring by index. The relational query is one plain join (the
  alternatives were rejected). `?start=&end=` hints are used by ClickHouse and Parquet only; a hint that finds nothing falls back to an
  unbounded read.
- **Export** streams NDJSON/CSV with no row cap, bounded by `ExportWindowGuard` and `ExportConcurrencyGate` (429). Readers advance row by row
  with `CommandTimeout = 0` and the request's token; trace export derives anchors once and fetches span counts per 1,000-anchor chunk.
  CSV cells pass `CsvFormulaGuard`.

### Summary rollups

Cards, charts and the error-rate/log-spike alerts read `request_rollup_minute` (inbound SERVER/CONSUMER spans: *requests*, with a 24-band
duration histogram, `DurationBands` is the one band definition) and `log_rollup_minute`. Rows are partial appends; reads `SUM`.
- Relational providers: `RollupAccumulator` is fed **only after a flush succeeds** and merges under one lock; `RollupWorker` (registered before
  the ingestion worker so it stops after the drain) appends closed minutes. ClickHouse and Parquet write rollups in their ingestion worker;
  their `IRollupStore` is a no-op.
- `RollupSummaryBuilder` clamps to `writtenThrough` and picks a bucket from a fixed ladder. **The API reads `Telemetry:Rollup`
  `FlushIntervalSeconds`/`CloseGraceSeconds` too: change them on both hosts.**
- MySQL alone has an hour tier (`request_rollup_hour`, `log_rollup_hour`, `rollup_compaction`, `MySqlRollupCompactor` driven by the API's
  `RollupCompactionWorker`); the column is `signal_kind` because `signal` is reserved.
- `SlowTraceEvaluator` stays on raw spans (`CountSlowInboundSpansAsync`); `TryClaimFireAsync` is an atomic UPDATE with a cooldown check.

### Retention

Per-tenant windows on the control-plane `tenants` row (30/30/30 defaults, no global default); telemetry with no `tenants` row expires on
`RetentionDefaults` (a test keeps it equal to the schema defaults). `RetentionWorker` (API host only, `AddRetention`) builds one `RetentionPolicy`
and calls `IRetentionSweeper`. Tenants to sweep come from the **telemetry** side, so orphans expire. Relational: per-tenant batched deletes via
`RetentionSweeperBase` (`BatchedDeleteSql`; MySQL reads metric ids first). ClickHouse: `DROP PARTITION` past the longest window, lightweight
deletes for shorter ones (`ClickHouseRetentionMemory`). Parquet: day folders. ClickHouse/Parquet collectors also drop expired records at ingest
(`RetentionWindowCache`).

### Providers that diverge

- **ClickHouse** (25.8, telemetry only): one row carries resource/scope attributes (`Map(LowCardinality(String), String)`, values as text); trace
  ids `UUID`, span ids `UInt64`, hex only at the edges (`ClickHouseIds`, never `hex()` in SQL). **A log time range needs a
  `toStartOfFiveMinutes(timestamp)` bound and a metric range a `toStartOfHour(time)` bound, or the sort key doesn't prune** (tests check
  `system.query_log` read rows). Inserts carry an `insert_deduplication_token` reused on retry; large flushes split into parallel pieces
  (`{token}:{table}:{i}`). Derived tables (`trace_index`, rollups, `metric_catalog`, `metric_series`) are written by the collector after the
  raw insert; no materialized views, no `FINAL`. Own ingestion worker (`ClickHouseIngestionWorker`, per-UTC-day buffers).
- **Parquet** (telemetry only, one machine): per tenant-hour Parquet files listed by per-writer manifests (atomic rename), read by an in-process
  DuckDB through `ParquetQueryEngine` (TEMP VIEWs over the resolved file list, never a glob; `NamedParameterConnection` rewrites `@x` to `$x`).
  **Readers list writers' manifests first, then the compacted generation** (the opposite order loses files). Trace ids are `trace_hi`/`trace_lo`
  `UBIGINT`; nested values are staged as JSON (the appender can't write MAP/STRUCT). `PartitionedIngestionWorker` (Core) buffers per tenant-hour
  (5 s linger, 50,000-record buffers); the Parquet registration raises the gate defaults so a buffer fits. Compaction runs on the API host under
  a lease. Debug log `parquet-sql ms= files= sql=` (category `ParquetQueryEngine`) is the slow-SQL source. `PoisonRecordTestsBase` doesn't run on it.
- **SQLite control plane**: `SqliteControlPlaneConnection` refuses relative paths and `:memory:`, forces foreign keys and pooling, and never
  lets the configured string decide the open mode. **Only the API host creates/migrates** (`SqliteControlPlaneInitializer` before any
  background service; forward-only chain with a `VACUUM INTO` backup); the collector and Admin tool check and fail. Timestamps are text in one
  layout (`SqliteTime.Format`) bound through `ControlPlaneRepositoryBase.TimestampParam`; `AUTOINCREMENT` so tenant ids are never reused.
  `SqliteScriptTests` holds the script to the PostgreSQL one. Collector-side reads append `Mode=ReadOnly`.
- **Relational physical layout:** SQL Server clusters hot tables on `(tenant_id, time, id)` / `(metric_id, time, id)`; MySQL makes those the
  primary key (plus `KEY (id)`) and sorts span rows by `(trace_id, span_id)` before insert (unordered inserts deadlock); PostgreSQL uses plain
  heaps (batched deletes go by `ctid`). Every relational index serves a named query; no GIN/trigram indexes.

### API and UI hosting

- **Routes.** `Telemetry:Api:BasePath` (default `/api`; `/` rejected) is added by `TelemetryApiConvention`, plus
  `tenants/{tenantId:long:min(1)}` for `[TenantScoped]` controllers. Controllers declare only their own segment. `MapKeryheTelemetryApi()` also
  maps a JSON 404 for unknown API paths.
- **Authorization** (off by default): every action carries exactly one `[TelemetryOperation]` (Read/Export/ManageAlerts/ManageSettings, never
  inferred from the verb) or `[TelemetryAnonymous]`; a test enumerates them. `TelemetryAuthorizationFilter` checks the operation policy and, on
  tenant routes, `TenantAccessRequirement`; a tenant refusal adds `X-Telemetry-Denied: tenant` (the UI keys on it). Writes without a bearer token
  need `X-Telemetry-Client: 1`. `AlertService.EvaluateAllAsync` sets `ITenantContext` itself, outside requests. The in-repo hosts are
  development examples with authorization off.
- **UI package** (`Keryhe.Telemetry.Ui`, a Razor class library whose csproj builds `src/telemetry-client` incrementally and warns, not fails,
  without Node). The client fetches `config.json` before bootstrapping, so nothing about the API location is compiled in.
  `UseKeryheTelemetryUi` rewrites `<base href>` for `BasePath` and serves the shell **from memory** (it owns `{BasePath}`, `{BasePath}/` and
  `{BasePath}/index.html`, content length of the encoded body, per-representation ETags); `TelemetryUiShell.Content` is null without a build and
  must 404, not throw. `AddKeryheTelemetryUi` is required (the guard is on the shell singleton, not on options). **The host must call
  `app.UseRouting()` explicitly right after `UseKeryheTelemetryUi()`**, and `MapKeryheTelemetryUiFallback()` must stay `MapFallback` scoped to
  `{BasePath}/{*path:nonfile}`. A reverse proxy must forward the base path, not strip it.
- UI pages live at `/t/:tenantId/...`; API services build URLs with `tenantApiUrl`; `authInterceptor` handles headers, 401 and tenant 403;
  `downloadBlob` stays a fetch so the interceptor runs for exports.

### Test data generator

`TenantSimulator` produces a neutral model for any range of virtual time, deterministically seeded from (seed, tenant, time). Two sinks:
**backfill** is hand-built OTLP through the Collector's gRPC stubs (`OtlpSink`, `MetricAggregator` for cumulative metrics), because the SDK
can't backdate logs or metrics; **live** is the real SDK (`SdkSink`), one provider set per pod, logs and measurements recorded while their span
is the current `Activity`. Its tests compare both sinks through an in-process fake collector.

## Default ports

Collector: h2c gRPC `http://localhost:5117` and OTLP/HTTP `http://localhost:5118` (Development), TLS `https://localhost:7057` (gRPC + OTLP/HTTP
by ALPN), management `http://127.0.0.1:5119` (`/healthz/live|ready`). Endpoints are named `Kestrel:Endpoints` that merge across appsettings
files by name. API: `http://localhost:5188`, `https://localhost:7105`; shipped `appsettings.json` sets `Telemetry:Api:BasePath=/telemetry/api`
and `TelemetryUi:BasePath=/telemetry`. Angular dev server: `http://localhost:4201`.

Connection strings come from user secrets (`ConnectionStrings:Collector`/`:Api`/`:ControlPlane`); the shipped appsettings set both providers to
`PostgreSQL` with empty strings.
