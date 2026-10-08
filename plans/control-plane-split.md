# Remove Timescale, split the control plane from telemetry data

Status: proposed 2026-10-07, reviewed and revised 2026-10-08, not started. No commits as part of this plan (the user
commits).

Two parts, in this order:

- **Part A** removes the TimescaleDB provider, so Part B has fewer scripts, fixtures and registrations to split.
- **Part B** separates the control-plane tables from the telemetry data tables: separate scripts, a separate
  provider key and a separate connection string.

Order of work: this plan, then `plans/list-caps.md`, then the ClickHouse redesign (`plans/clickhouse-row-model.md`),
then a Parquet + DuckDB telemetry provider (planned separately; Part B is its prerequisite, because Parquet cannot hold
mutable control-plane state). ClickHouse stays.

This plan and `plans/list-caps.md` ship as **one 4.0.0 release**. List-caps edits the `*-Telemetry.sql` scripts this
plan creates and keeps the version at 4.0.0, so 4.0.0 is not recorded as released, and no database is treated as
"4.0.0", until both are done.

## Decisions

| # | Decision |
|---|---|
| 1 | Remove TimescaleDB entirely (project, schema script, fixtures, tests, harness, docs). There is no migration: a Timescale deployment reinstalls on PostgreSQL. |
| 2 | Control-plane tables: `tenants`, `api_keys`, `alert_rules`, `alert_events`, `retention_settings`. Everything else is telemetry: `resources`, `instrumentation_scopes`, `spans`, `log_records`, `metrics`, the five data-point tables, `metric_last_seen`, the rollup tables, MySQL's `rollup_compaction`, and ClickHouse's `trace_index`. |
| 3 | The control plane is relational only: PostgreSQL, SQL Server and MySQL. ClickHouse has no control-plane script; a ClickHouse deployment also runs one of the three for its control plane. ClickHouse's best-effort control-plane code (mutation-based alert CRUD, non-atomic `TryClaimFireAsync`, `FINAL` key lookup, no-op key touch, `ALTER TABLE retention_settings UPDATE`) is deleted. |
| 4 | New required key `ControlPlane:Provider` (`PostgreSQL` \| `SqlServer` \| `MySql`), separate from `Database:Provider`, on both hosts. Missing or unknown values fail startup with the key named. |
| 5 | New required connection string `ConnectionStrings:ControlPlane` on both hosts. There is no fallback to `Collector`/`Api`; startup fails with the key named. For the relational providers it points at the same database as the data strings. |
| 6 | Control-plane implementations live in the existing PostgreSQL, SqlServer and MySql provider projects, behind a new pair `Add<Provider>ControlPlaneCollectorServices(configuration)` / `Add<Provider>ControlPlaneApiServices(configuration)`, mirroring the data side. Interfaces stay in Core. No new project. A ClickHouse consumer references one relational package as well. A single method for both hosts is not possible: the alert-rule repositories need `ITenantContext`, which only the API registers, and Development's build-time DI validation would fail the collector's startup. |
| 7 | Fresh install only. There is no upgrade from 3.2.1; existing tenants and keys are re-created with the Admin tool. |
| 8 | Schema versioning: two components, `control-plane` and `telemetry`, both 4.0.0, released together with `plans/list-caps.md`'s schema change (see "Order of work"). After that release each bumps only its own third digit. Versioned projects go to 2.0.0. |
| 9 | Version tables: `control_plane_schema_version` and `telemetry_schema_version`, each created by its own script. There is no shared `schema_version` table. |
| 10 | The Admin tool talks to the control plane only and supports PostgreSQL, SQL Server and MySQL (MySQL added, Timescale removed). It reads `ControlPlane:Provider` and `ConnectionStrings:Admin`. Its `AdminProvider` values are exactly the `ControlPlane:Provider` values, so the two lists cannot drift. |
| 11 | The per-tenant retention sweep (PostgreSQL, SQL Server, MySQL) gets its tenant list from the data side, `SELECT DISTINCT tenant_id FROM resources`, not from `tenants`. Data whose tenant no longer exists in the control plane still expires. |
| 12 | Cross-boundary foreign keys are removed: `resources.tenant_id -> tenants` on all three relational scripts (inline `REFERENCES tenants` on PostgreSQL and SQL Server, the named `fk_resources_tenants` constraint on MySQL). FKs inside one side stay (`api_keys`, `alert_rules` -> `tenants`; `alert_events` -> `alert_rules`; `metrics` -> `resources`/scopes). |
| 13 | No query joins across the boundary. Today none does (verified: alert evaluation reads rules, then calls data repositories separately; the tenant catalog reads only `tenants`; the only crossing read is decision 11's `SELECT id FROM tenants`). |

## Part A: remove TimescaleDB

### A1. Code

- Delete `src/Keryhe.Telemetry.Timescale` and remove it from `Telemetry.sln` and from the `ProjectReference`s in
  `Api.Server` and `Collector.Server`.
- Remove the `"Timescale"` case from both hosts' `Program.cs` switches and update their error messages.
- Admin: remove `AdminProvider.Timescale` and its `Program.cs` case; update `NpgsqlAdminRepository`'s doc comment.
- Core and providers: the Timescale mentions are comments, apart from these extension points:
  - `TraceReadRepositoryBase`'s hint hook (`HintedTraceTimeBounds`) stays, because ClickHouse uses it.
  - Any `virtual` or override that existed only for Timescale gets removed. Grep for `Timescale` in
    `DapperReadRepository`, `TraceReadRepositoryBase`, `RetentionSettingsRepositoryBase`,
    `ProviderCapabilities`, `ITraceReadRepository`, `IRetentionSettingsRepository`, `TracesController`,
    `PostgreSqlBulkWriter` and `PostgreSqlReadRepositories`, and decide per item.
  - Comments are reworded rather than left naming a provider that no longer exists.
- UI (`traces-api.service.ts`, `trace-detail`, `trace-list`, `dashboard`): the `?start=&end=` hint stays for
  ClickHouse; only comments change.

### A2. Schema

- Delete `schema/Timescale-Schema.sql`; remove `timescale` from `apply-schema.sh`.

### A3. Tests and harness

- Integration tests: delete `TimescaleFixture` and every `Timescale*Tests` (including `TimescalePlanTests.cs`) and
  `TimescalePlanSurvey`. Remove the
  provider from `Collections.cs` and `ProviderNames.cs`, and from the per-provider lists in `CapabilitiesTests`,
  `DatabaseObserverTests`, `ScenarioTests`, `TestInfrastructureTests`, `RetentionTestsBase`, `TracePhase3TestsBase`,
  `LogPhase2TestsBase`, `SummaryTimeoutDegradationTestsBase` and `TimedQueryProviderTestsBase`.
- `PostgresFamilyPlanTestsBase` now has one subclass. Fold it into the PostgreSQL plan tests unless keeping the base
  is simpler.
- Benchmarks (`TraceQueryBench`, `RollupQueryBench`): drop the Timescale branches.
- TestInfrastructure: delete `TimescaleProviderContainer`; simplify `PostgresFamilyContainer` and
  `ProviderContainerFactory`; remove the Timescale image reference from the csproj.
- Stress harness: remove Timescale from `RunCommand`'s usage text and the provider list, `ScenarioProfile`,
  `HistorySeeder`, `MarkerProbe`, `DatabaseObserverFactory`/`PostgresObserver` (Timescale branches only) and
  `HtmlReportWriter`.

### A4. Docs

- Update `README.md`, `CLAUDE.md`, `docs/SETUP.md`, `docs/CONFIGURATION.md`, `src/Keryhe.Telemetry.Collector/README.md`,
  `src/Keryhe.Telemetry.Core/README.md` and `tests/Keryhe.Telemetry.StressTests/README.md`.
- In `CLAUDE.md`, also remove the provider-list, retention-table and index notes that describe Timescale, and the
  links to `plans/*.md` files that no longer exist (they are archived outside the repo).

### A5. Verification

- `dotnet build Telemetry.sln` is clean, and `grep -ri timescale` over `src`, `tests`, `schema` and the docs finds
  nothing but intentional historical notes, if any.
- `dotnet test tests/Keryhe.Telemetry.IntegrationTests` passes on the remaining four providers, plus the `ApiHttp`
  and `CollectorAuth` suites (no Docker).

## Part B: split the control plane

### B1. Core interfaces

- **Control-plane interfaces** (implemented by PostgreSQL, SqlServer and MySql only):
  - `IApiKeyLookup` and `IApiKeyTouchStore` (collector)
  - `IAlertRuleRepository`, `ITenantCatalogRepository` and `IRetentionSettingsRepository` (API), the latter reduced
    to `GetSettingsAsync`/`UpdateSettingsAsync`
- **New telemetry interface `IRetentionSweeper`**: `DeleteOldTracesAsync`, `DeleteOldMetricDataPointsAsync`,
  `DeleteOldLogRecordsAsync`, implemented by every telemetry provider (PostgreSQL, SqlServer, MySql, ClickHouse).
  - `RetentionSettingsRepositoryBase` splits into `RetentionSettingsRepositoryBase` (settings) and
    `RetentionSweeperBase` (the batched per-tenant delete loop, `BatchedDeleteSql`).
  - The sweeper's tenant list comes from `resources` (decision 11).
- `RetentionWorker` reads settings from `IRetentionSettingsRepository` and calls `IRetentionSweeper`.
  `SettingsController` is unchanged apart from its dependency.
- **Control-plane base classes.** `AlertRuleRepositoryBase` derives from `DapperReadRepository`, which carries
  telemetry-read machinery (the `asOf` expression, `IdParam`, `IdInPredicate`). It moves to a small
  `ControlPlaneRepositoryBase` in Core holding only `OpenConnectionAsync` and the `ExecuteWithRetryAsync` hook.
  SQL Server's override (one retry on error 1205) stays, so `TryClaimFireAsync` and rule CRUD keep it.
  `TenantCatalogRepositoryBase` and the settings half of `RetentionSettingsRepositoryBase` derive from it as well.
- `AlertService`, the evaluators, `TelemetryAuthorizationFilter` / `ClaimMappedTenantAccessHandler` and
  `TelemetryApiStartupValidator` keep their current interfaces; only the registrations behind them move.

### B2. Provider registration

- Each relational provider gains a pair (decision 6), both against `ConnectionStrings:ControlPlane`:
  - `Add<Provider>ControlPlaneCollectorServices`: `IApiKeyLookup`, `IApiKeyTouchStore`.
  - `Add<Provider>ControlPlaneApiServices`: `IAlertRuleRepository`, `ITenantCatalogRepository`,
    `IRetentionSettingsRepository`.
  - Each reads and checks `ConnectionStrings:ControlPlane` **when it is called** (registration time), throwing with the
    key named if it is missing (decision 5). The repositories receive the string through a small options/holder
    singleton rather than calling `GetConnectionString` in their constructors, which would only fail on first use.
- `Add<Provider>CollectorServices` / `Add<Provider>ApiServices` stop registering control-plane interfaces; they
  register `IRetentionSweeper` (API side) instead.
- **PostgreSQL connection pools.** The PostgreSQL provider registers a singleton `NpgsqlDataSource` per side. With
  PostgreSQL as both the telemetry and control-plane provider, two data sources would claim the same service type.
  - Change: the control plane uses a **keyed** `NpgsqlDataSource` (key `"ControlPlane"`), and these classes take it
    through `[FromKeyedServices("ControlPlane")]`: `TenantResolver`, `PostgreSqlApiKeyTouchStore`,
    `PostgreSqlAlertRuleRepository`, `PostgreSqlTenantCatalogRepository`, `PostgreSqlRetentionSettingsRepository`.
  - The data side keeps the unkeyed registration. This also covers ClickHouse + PostgreSQL control plane.
  - A class that is missed would silently use the telemetry pool, which still works when both sides share one
    database. Guard against it: the ClickHouse + PostgreSQL fixture exercises every control-plane class (B5), and
    there the PostgreSQL provider registers no unkeyed `NpgsqlDataSource` at all, so a missed class fails to resolve.
- **SqlServer and MySql** open connections from the string per operation. Each control-plane class currently reads
  `GetConnectionString("Api")` or `("Collector")` in its own constructor and changes to the holder above:
  `TenantResolver` / `MySqlTenantResolver`, `SqlServerApiKeyTouchStore` / `MySqlApiKeyTouchStore`,
  `*AlertRuleRepository`, `*TenantCatalogRepository`, `*RetentionSettingsRepository` (settings half).
- ClickHouse: delete `TenantResolver` (the `IApiKeyLookup`), `ClickHouseApiKeyTouchStore`,
  `ClickHouseAlertRuleRepository`, `ClickHouseTenantCatalogRepository` and the settings half of
  `ClickHouseRetentionSettingsRepository`. Its `DROP PARTITION` sweep becomes `ClickHouseRetentionSweeper`.
  Any `ClickHouseIds` use that exists only for control-plane rows goes too.

### B3. Hosts and Admin

- Both hosts get a second switch, on `ControlPlane:Provider`: the collector calls
  `Add<Provider>ControlPlaneCollectorServices`, the API `Add<Provider>ControlPlaneApiServices`.
  - Missing or unknown values throw, naming the key (decision 4).
  - A missing `ConnectionStrings:ControlPlane` throws, naming the key (decision 5), from the `Add*ControlPlane*`
    methods themselves (B2), so a consumer's own host gets it too, not only the in-repo hosts.
- Shipped `appsettings.json` / `appsettings.Development.json` set `ControlPlane:Provider`. Connection strings
  stay in User Secrets, per the existing convention; document the new secret.
- Admin:
  - Switches on `ControlPlane:Provider`.
  - Adds `MySqlAdminRepository` (the `AdminRepositoryBase` shape, MySQL dialect).
  - Drops the "MySQL and ClickHouse: use SQL" rejection message.
  - Updates `docs/SETUP.md`'s SQL-only path: the manual SQL stays documented only as an alternative.
- `scripts/new-api-key.sh` / `New-ApiKey.ps1`: no logic change; their INSERT examples target the control-plane
  database.

### B4. Schema scripts

- **Rename and split:**
  - `schema/PostgreSQL-ControlPlane.sql`, `schema/SqlServer-ControlPlane.sql`, `schema/MySQL-ControlPlane.sql`:
    the five control-plane tables, the `retention_settings` seed row and `control_plane_schema_version`
    (4.0.0, decisions 8 and 9).
  - `schema/PostgreSQL-Telemetry.sql`, `schema/SqlServer-Telemetry.sql`, `schema/MySQL-Telemetry.sql`,
    `schema/ClickHouse-Telemetry.sql`: everything else, plus `telemetry_schema_version` (4.0.0).
- The `resources.tenant_id` FK is dropped (decision 12); its `DEFAULT 1` goes too (a default tenant id is meaningless
  without the FK and hides a missing value).
- **Database-level settings:**
  - SQL Server's `ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON` stays in the telemetry script, where the
    export reads need it. The control-plane script also sets it, since it is idempotent and harmless when the two
    share a database, and correct when they don't.
  - Any other database-level statements (MySQL session settings, PostgreSQL extensions) are reviewed the same way.
- ClickHouse's control-plane tables are removed from its script.
- **`apply-schema.sh`:**
  - It becomes `apply-schema.sh <controlplane|telemetry> <provider> [database]`, with one `TARGET_VERSION` per
    component, and checks its component's version table.
  - `controlplane` accepts only `postgresql|sqlserver|mysql`.
  - A schema change edits the affected component's scripts plus that component's `TARGET_VERSION`, in one commit.
- Order on a shared database: either order works (no FK crosses, no shared objects). Document control plane first,
  since a running collector needs keys before it accepts data.

### B5. Tests and harness

- TestInfrastructure:
  - `ProviderContainer` gains `ControlPlaneConnectionString` and seeds tenants/keys there (`SeedTenantAsync`).
  - The relational containers apply both scripts to the one `telemetry` database and return the same string for
    both.
  - `ClickHouseProviderContainer` composes a PostgreSQL container for its control plane (`postgres` image already
    used by the PostgreSQL fixture), started and disposed with it. A ClickHouse-only run
    (`--filter Provider=ClickHouse`) therefore starts PostgreSQL too; document it in the test README.
  - `ContainerOptions` diagnostics/limits apply to the ClickHouse container only.
- `ProviderFixture` adds `ControlPlane:Provider` and `ConnectionStrings:ControlPlane` to its configuration.
- Tests to move or add:
  - `*ApiKeyLookupTests`: ClickHouse's is deleted (there's no ClickHouse lookup); the relational ones point at the
    control-plane string.
  - Alert-rule and retention-settings tests run per control-plane provider; retention *sweep* tests stay per
    telemetry provider.
  - New: a retention sweep test for decision 11, per relational telemetry provider. Seed expired spans, logs and data
    points through the bulk writer for a tenant id that has **no `tenants` row** (the tables share one database in
    these fixtures, so the absence must be explicit: use an id `TenantSeeder` never creates), alongside a seeded tenant
    with expired data. Assert both tenants' expired rows are gone. Negative control: switch the sweeper's tenant query
    back to `SELECT id FROM tenants` and confirm the unregistered tenant's rows survive.
  - New: a ClickHouse fixture test that every control-plane class runs on PostgreSQL: key lookup and touch, tenant
    catalog, retention settings get/update, alert CRUD and `TryClaimFireAsync` (atomic claim under two concurrent
    claimers). With no unkeyed `NpgsqlDataSource` registered there (B2), this also proves every class takes the keyed
    one.
  - New: startup fails naming the key for missing `ControlPlane:Provider`, missing `ConnectionStrings:ControlPlane`
    and an unknown provider, on both hosts (no Docker).
  - `HostOrchestrationTests`: both hosts now receive `ConnectionStrings__ControlPlane`.
  - `ApiHttp` / `CollectorAuth`: register fakes for the control-plane interfaces the same way they do now; adjust to
    the `IRetentionSweeper` split.
- Stress harness:
  - `HostLauncher` passes `ConnectionStrings__ControlPlane` and `ControlPlane__Provider` to both hosts.
  - For ClickHouse it starts the PostgreSQL control-plane container.
  - The database observer watches the telemetry database only (control-plane load is negligible); the report states
    which control-plane database ran and, for ClickHouse, that its PostgreSQL container shared the Docker VM's CPUs
    (it is not covered by `--db-cpuset`).
- TestDataGenerator: no change (it holds keys only).

### B6. Docs

- **`docs/CONFIGURATION.md`:**
  - New keys `ControlPlane:Provider` and `ConnectionStrings:ControlPlane` (both required, both hosts, plus Admin's
    provider key).
  - Remove `schema_version`.
- **`docs/SETUP.md`:**
  - Apply both scripts.
  - Same-database setup for relational providers.
  - A separate control-plane database for ClickHouse.
  - Admin tool for all three.
- **`README.md`, the provider READMEs and the Collector/API READMEs:**
  - Consumer composition: two provider registrations, and a ClickHouse consumer adds a relational package.
  - Key revocation: no more ClickHouse `mutations_sync` note.
- **`CLAUDE.md`:** the commands block (schema runner, Admin), "Provider abstraction", "ClickHouse provider notes"
  (control-plane-is-best-effort paragraph removed), "Collector authentication", "Alerting", "Retention",
  "Admin tool" and "Database" (table lists by component, version tables, the dropped FK). It also states the
  versioning rule (decision 8).
- **Code comments** naming `ConnectionStrings:Api`/`Collector` for control-plane concerns:
  `src/Keryhe.Telemetry.Api.Server/Program.cs` (retention settings), `TelemetryApiExtensions.cs`,
  `IRetentionSettingsRepository`'s doc comment, and the provider `ServiceCollectionExtensions` summaries.

### B7. Verification

- `dotnet build Telemetry.sln` is clean.
- `dotnet test tests/Keryhe.Telemetry.IntegrationTests` passes on all four providers, ClickHouse with its PostgreSQL
  control plane, plus `ApiHttp` and `CollectorAuth`.
- Grep: no telemetry provider registers a control-plane interface, and no control-plane SQL appears outside the
  three relational providers and Admin.
- Manual, on the dev machine:
  1. Recreate the dev database with both scripts.
  2. Create a tenant and key with the Admin tool.
  3. Run both hosts and the TestDataGenerator.
  4. Check the UI pages, alert CRUD and the retention settings page.
- Repeat with ClickHouse for telemetry and PostgreSQL for the control plane (Docker; see the ClickHouse
  verification setup).
- Stress smoke: `--provider all --topology split --profile smoke`. It isn't a performance comparison with earlier
  runs (different schema); it checks that every provider still runs end to end.

## Risks and notes

- **Consumers must change config.** Two required keys are breaking by design (decisions 4 and 5): an upgraded host
  without them fails at startup with the key named, never silently.
- **Tenant deletion.** Nothing deletes a tenant today (Admin deletes keys, not tenants). If tenant deletion is added
  later, its telemetry stays until retention removes it; that is the intended consequence of decision 11, not a leak.
- **Two databases, no transaction across them.** Nothing needs one today (decision 13). A future feature that writes
  both sides atomically would need its own design.
- **Collector availability.** With ClickHouse plus a separate control-plane database, the collector's key lookup
  depends on that database.
  - The existing cache (positive 30 s, negative 5 s) and the `UNAVAILABLE` response on a failed lookup already cover
    an outage: exporters retry.
  - Document that a control-plane outage longer than the positive TTL stops ingestion for keys not in the cache.
