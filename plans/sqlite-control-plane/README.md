# SQLite control plane

Status: **implemented 2026-10-24** (uncommitted). Where the build differs from, or adds to, what is written below:

- The SQLite control-plane tests (`SqliteControlPlaneTests`, `SqliteApiKeyLookupTests`) run on the Parquet fixture's SQLite file rather than a fixture of their own;
  `SqliteSchemaTests`, `SqliteMigrationTests`, `SqliteScriptTests` and `SqliteAdminTests` need no fixture. The host-environment test is in `ScenarioTests`, not `HostOrchestrationTests`.
- `Microsoft.Data.Sqlite` 10.0.5 pulls `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which NuGet flags (GHSA-2m69-gcr7-jv3q, <= 2.1.11); the library and the Admin tool reference 2.1.13 directly.
- `ProviderContainer.UseSqliteControlPlane(dir)` (not only `ParquetProviderContainer`) moves any provider's control plane to a SQLite file, and `--control-plane` can say `sqlite` for every provider.
  A choice that moves a provider off its default tags the scenario (`-cpsqlite`, `-cppostgresql`) and `scenario.json` records `controlPlane`; there is no cross-run comparison, so "never pairs" is
  enforced only inside one run (the read/write isolation table requires equal control planes).
- The two recorded smoke runs are in `stress-results/sqlite-cp-smoke-default` (SQLite) and `stress-results/sqlite-cp-smoke-postgresql` (gitignored). Not run: a ramp, a relational provider with `--control-plane sqlite`
  end to end (its wiring is covered by factory and environment tests only), a Windows service install.
- Five `ScenarioTests` fail independently of this work (the committed profiles have `retentionIntervalSeconds: 86400`, and one test still expects four providers).

Goal: `ControlPlane:Provider=Sqlite` holds tenants (with their retention windows), API keys, alert rules and alert events in
one SQLite file, so a Parquet deployment needs no database server at all. SQLite is a control-plane provider only: it is
never a `Database:Provider`.

## Decisions

Asked and answered (2026-10-24):

| # | Decision |
|---|---|
| D1 | **Schema creation.** `schema/SQLite-ControlPlane.sql` is the source of truth and is also **embedded in `Keryhe.Telemetry.Sqlite`**. The **API host applies it at startup**, idempotently, and applies any migrations (D8) unless `ApplySchema` is off (D9). The **collector never creates or migrates anything**: it fails startup and exits when the database is not there or not current (D6). `apply-schema.sh controlplane sqlite <file>` can also create a fresh file ahead of time (needs the `sqlite3` CLI); it never migrates |
| D2 | **Pairing.** Allowed with every `Database:Provider` (PostgreSQL, SQL Server, MySQL, ClickHouse, Parquet). The docs state the limit: every process that opens the file (collector, API, Admin tool) runs on **one machine's local file system**. That is the Parquet root's rule already, and it rules out load-balanced hosts on separate machines |
| D3 | **Package.** A new project and NuGet package, `Keryhe.Telemetry.Sqlite`, control plane only: `AddSqliteApiKeyLookup` / `AddSqliteControlPlaneManagement`, the same pair the relational providers expose |
| D4 | **Tests.** `SqliteControlPlaneTests` runs `ControlPlaneTestsBase` against a temp file, with no Docker. **Every Parquet test defaults to a SQLite control plane**: `ParquetProviderContainer`, so the `Parquet` and `ParquetCompacted` fixtures (and every suite that runs on them: retention, rollups, control-plane registration of a Parquet host), and the stress harness's Parquet runs. `--filter Provider=Parquet` then needs no Docker at all. The harness gains `--control-plane <postgresql\|sqlite>` to override the default; ClickHouse runs keep PostgreSQL, and the relational providers keep their own database |
| D5 | **Version skew is forward only.** At API startup, with the recorded version being the highest row in `control_plane_schema_version`: no version row → run the full create script; this build's version → do nothing; **older** → apply each migration in order up to this build's version (D8); older but with **no complete chain** of migrations from it → startup fails naming the recorded version and the missing step; **newer** → startup fails naming both versions (no downgrades). No migration scripts exist yet, so today an older version always takes the "no chain" branch |
| D6 | **"Not found"** (collector and Admin tool): the file is opened **without permission to create it** (`Mode=ReadWrite`; Microsoft.Data.Sqlite creates a missing file by default), and the check fails when the file is missing **or** its recorded version is not exactly this build's, which also covers an API that has not finished creating or migrating it. The message says to start the API host once (or, for a missing file, run `apply-schema.sh`). Upgrade order is therefore API first, then the collector and the Admin tool |
| D7 | **Timestamps** are `TEXT` in UTC, in exactly one layout: `yyyy-MM-dd HH:mm:ss.fffffff`. Column defaults and SQL-side "now" produce the same layout, so comparisons are plain text comparisons (the alert cooldown claim). `DateTimeOffset` parameters are converted to a UTC `DateTime` first, so no `+00:00` suffix ever lands in a column |
| D8 | **Forward-only migrations, SQLite only.** The project rule "no migration scripts" is relaxed **for the SQLite control plane alone**: the other providers stay fresh-install only. A future control-plane schema change edits the full create script (fresh installs keep using it) **and** adds one migration script from the previous version. Everything that creates, checks or migrates lives in **`Keryhe.Telemetry.Sqlite`** as public API, so any host can use it. For now only the API host creates or migrates |
| D9 | **`Telemetry:ControlPlane:Sqlite:ApplySchema`** (default `true`), read by `AddSqliteControlPlaneManagement`. `false` makes the API host only check, as the collector does (D6), for an operator who wants to create or migrate deliberately, for example after taking their own backup. The collector has no such option: it always only checks |

Chosen without asking (each has a reason; say if you want it otherwise):

| # | Choice | Reason |
|---|---|---|
| X1 | Config value `Sqlite`; script `schema/SQLite-ControlPlane.sql`; namespace `Keryhe.Telemetry.Sqlite` | Matches the existing casing: `MySql`/`SqlServer` for config values, `MySQL-`/`PostgreSQL-` for script names |
| X2 | Driver **Microsoft.Data.Sqlite** (with its bundled `e_sqlite3`), version 10.0.x to match the other Microsoft.Extensions packages | It's Microsoft's ADO.NET provider, it ships the native library for every RID the hosts publish to, and Dapper already works with it. System.Data.SQLite would add a second native-loading story next to DuckDB's |
| X3 | Control-plane schema version **stays 4.0.1**; the SQLite script records `4.0.1` | The version tracks the logical schema, and a fourth script with the same tables and columns doesn't change that. `CONTROLPLANE_TARGET_VERSION` is unchanged |
| X4 | `INTEGER PRIMARY KEY AUTOINCREMENT` on every id | Without `AUTOINCREMENT`, SQLite reuses the highest rowid after a delete. Telemetry rows carry `tenant_id` with no foreign key, so a reused tenant id would hand a deleted tenant's data to a new tenant. PostgreSQL identities never reuse, and neither may this |
| X5 | `STRICT` tables, `CHECK (length(x) <= n)` where the other scripts have `VARCHAR(n)`, `CHECK (json_valid(...))` on the JSON columns | SQLite ignores declared types and lengths otherwise. These keep the same rows valid on all four providers |
| X6 | The connection string must name an **absolute path**; `:memory:` and relative paths fail startup | The collector and the API have different content roots and working directories, so a relative path would open two different files without any error |
| X7 | The API creates the **file** but not its **directory**; a missing directory fails startup | Creating directories from a mistyped path would silently split the hosts onto two databases |
| X8 | The provider sets `Foreign Keys=True` and `Pooling=True` on every connection string it builds, whatever the configured string says; WAL is set by the script (`PRAGMA journal_mode = WAL` is stored in the file) | `ON DELETE CASCADE` (the Admin tool's tenant and key deletes) only works with foreign keys on, which SQLite turns off by default for each connection. WAL lets the collector's reads run while the API writes |
| X9 | `Default Timeout` (Microsoft.Data.Sqlite's busy wait) defaults to 30 s unless the connection string sets it | Writers (key touch, alert claim, retention-settings update, Admin) serialize on the file's write lock, and a busy statement should wait rather than fail. All of these writes are tiny |

## Schema: `schema/SQLite-ControlPlane.sql`

The same logical tables, columns, defaults, indexes and foreign keys as `MySQL-ControlPlane.sql`, translated:

- `PRAGMA journal_mode = WAL;` first, outside any transaction (the only statement that can't sit inside one).
- `BIGINT AUTO_INCREMENT PRIMARY KEY` → `INTEGER PRIMARY KEY AUTOINCREMENT` (X4); `TINYINT(1)` → `INTEGER NOT NULL DEFAULT 1 CHECK (x IN (0, 1))`;
  `JSON` → `TEXT CHECK (json_valid(x))`; `VARCHAR(n)`/`CHAR(64)` → `TEXT CHECK (length(x) <= n)` (X5).
- Timestamps `TEXT`, defaults `DEFAULT (strftime('%Y-%m-%d %H:%M:%f0000', 'now'))`, which gives the D7 layout (SQLite's `%f` is
  `SS.SSS`; the literal `0000` pads it to seven fractional digits). The default and the .NET format give strings of the same
  length and layout, which is what makes text comparison correct.
- Every `CREATE` is `IF NOT EXISTS`. The version row is `INSERT ... ON CONFLICT (version) DO UPDATE SET applied_at = ...`, last,
  as in the other scripts.
- `alert_rules (tenant_id, enabled)` becomes a partial index `WHERE enabled = 1` (SQLite has them, like PostgreSQL).

The embedded copy is the same file, linked into the assembly as an `EmbeddedResource` (`<EmbeddedResource Include="../../schema/SQLite-ControlPlane.sql" LogicalName="..." />`),
so it can't drift from the copy on disk.

## Migrations (D8)

None exist yet; this is the mechanism they will use.

- **Files**: `schema/SQLite-ControlPlane-Migrations/<from>-to-<to>.sql` (for example `4.0.1-to-4.0.2.sql`), embedded by a wildcard
  `EmbeddedResource` item, so adding a file is all it takes to ship one.
- **What a migration script may contain**: plain DDL and DML. It must not contain `BEGIN`/`COMMIT`, `PRAGMA foreign_keys` or the
  version row; the runner owns all three. A test enforces this for every embedded migration.
- **The chain**: versions compare as `System.Version`. The runner builds the path from the recorded version to the target by
  following `<from>` → `<to>`. A missing step, two scripts with the same `<from>`, or a step that goes backwards fails startup naming
  the files involved.
- **A run**, in `SqliteControlPlaneSchema.EnsureCurrentAsync`:
  1. Read the recorded version without a lock. If it's current, stop.
  2. If migrations are needed: back up with `VACUUM INTO '<file>.bak-<from version>-<UTC yyyyMMddHHmmss>'`. `VACUUM INTO` can't run inside
     a transaction, and the timestamp keeps a second API instance's backup or a retry from colliding with an earlier one.
  3. `PRAGMA foreign_keys = OFF` on this connection (it can't change inside a transaction, and SQLite's table rebuild needs it off),
     then `BEGIN IMMEDIATE`.
  4. Re-read the version inside the transaction, since another API instance may have migrated in the meantime, and work out the
     steps still needed from it.
  5. Run each step's script, inserting that step's version row after it, so `control_plane_schema_version` is the upgrade history.
  6. `PRAGMA foreign_key_check`; any row → roll back and fail naming the table. Otherwise commit, then `PRAGMA foreign_keys = ON`.
  
  Any failure rolls the whole run back (SQLite DDL is transactional), leaves the file at its old version and keeps the backup. The
  log line names the step that failed and the backup's path.
- **Fresh installs** run the full create script, never the chain. The "fresh equals migrated" test (below) is what keeps the two in step.
- **`PRAGMA user_version` is not used**: the version table keeps the history, and `apply-schema.sh` already reads it.

## The provider: `src/Keryhe.Telemetry.Sqlite`

csproj like `Keryhe.Telemetry.MySql`'s (version 2.0.1, package metadata, README packed), references Core, Dapper,
Microsoft.Data.Sqlite, Microsoft.Extensions.Hosting.Abstractions (for the startup services). Added to `Telemetry.sln`.

| Type | Role |
|---|---|
| `SqliteControlPlaneConnection` | Built once at registration from `ControlPlaneConnection.FromConfiguration` (which names the key when missing). Parses with `SqliteConnectionStringBuilder`, enforces X6, applies X8/X9, exposes `Open(SqliteOpenMode)` / `OpenAsync`. All repositories take it, so no class reads `IConfiguration` |
| `SqliteTime` | The one place for D7: the format string, `ToColumn(DateTime)`/`ToColumn(DateTimeOffset)` (UTC, fixed layout), `FromColumn(DateTime)` (`SpecifyKind(Utc)`), and the SQL expression for "now" and "now minus n minutes" |
| `SqliteApiKeyLookup : IApiKeyLookup` | The same `SELECT id, tenant_id, expires_at ... WHERE key_hash = @keyHash AND is_active = 1 LIMIT 1`; `expires_at` stamped UTC |
| `SqliteApiKeyTouchStore : IApiKeyTouchStore` | `UPDATE api_keys SET last_used_at = <now> WHERE key_hash IN (...)` in chunks of 500 (SQLite's default variable limit is 32,766, so 500 leaves plenty of room), one transaction for all chunks so a touch takes the write lock once |
| `SqliteAlertRuleRepository : AlertRuleRepositoryBase` | `JsonParam` = `@p`; `ReturningIdentity` = ` RETURNING id`; `ClaimFireSql` = `UPDATE alert_rules SET last_fired_at = <now> WHERE id = @ruleId AND tenant_id = @tenantId AND enabled = 1 AND (last_fired_at IS NULL OR last_fired_at < <now minus @cooldownMinutes>)`. A single `UPDATE` is atomic under SQLite's one writer, which is what `TryClaimFireAsync` needs. `createdAt`/`firedAt` parameters go through `SqliteTime.ToColumn` (one override each, or a hook in the base for how a timestamp parameter is bound; the relational providers' hook returns the value unchanged) |
| `SqliteTenantCatalogRepository`, `SqliteRetentionSettingsRepository` | Connection only (their SQL is dialect-neutral). The retention update's `@updatedAt` goes through the same timestamp hook |
| `SqliteControlPlaneSchema` (**public**) | The embedded create script and migrations, `TargetVersion = "4.0.1"`, and the two entry points any host can call: `EnsureCurrentAsync(SqliteControlPlaneConnection, ILogger, ct)`, which creates or migrates (D5, "Migrations"), and `CheckAsync(...)`, which verifies only (D6) and returns a result naming what's wrong. All the version logic lives here; the hosted services below only call it |
| `SqliteControlPlaneOptions` | `ApplySchema` (D9), bound from `Telemetry:ControlPlane:Sqlite` with `IOptions` |
| `SqliteControlPlaneInitializer : IHostedLifecycleService` (API) | In `StartingAsync`, before any `BackgroundService` starts (alerting, retention). With `ApplySchema` on: opens with `ReadWriteCreate` (directory must exist, X7) and calls `EnsureCurrentAsync`. Creation is one `BEGIN IMMEDIATE` transaction (WAL pragma first), so two API processes starting together serialize and a collector never sees half a schema. With `ApplySchema` off: opens with `ReadWrite` and calls `CheckAsync`, failing like the collector |
| `SqliteControlPlaneStartupCheck : IHostedLifecycleService` (collector) | In `StartingAsync`, before Kestrel binds: opens with `ReadWrite` and calls `CheckAsync` (D6); on failure it throws, the host stops with a non-zero exit code, and the log line names the file and the fix. Same shape as `PlaintextTransportGuard` |
| `SqliteFlushErrorClassifier` | Not needed: the control plane has no flush path. Listed so nobody adds one |

Registration:

- `AddSqliteApiKeyLookup`: `SqliteControlPlaneConnection`, `IApiKeyLookup`, `IApiKeyTouchStore`, the startup check.
- `AddSqliteControlPlaneManagement`: `SqliteControlPlaneConnection`, `SqliteControlPlaneOptions`, `IAlertRuleRepository`,
  `ITenantCatalogRepository`, `IRetentionSettingsRepository`, the initializer.

A consumer's own host gets this behaviour from the same two registrations, or calls `SqliteControlPlaneSchema` directly (for
example from a deployment step) with `ApplySchema` off.

Dapper mapping to verify in the tests (not to assume): `INTEGER` → `bool` (`Enabled`, `is_active`), `INTEGER` → `int`
(`AlertRule.Id`), `TEXT` → `DateTime` (Microsoft.Data.Sqlite parses it; `Kind` comes back `Unspecified`, so the DTOs read it as UTC
the way MySQL's `AsUtc` does).

## Host and Core changes

- **Both `Program.cs` switches** gain `case "Sqlite"`, and the error messages list it (`expected SqlServer, PostgreSQL, MySql, or Sqlite`).
  Both hosts get a `ProjectReference` to `Keryhe.Telemetry.Sqlite`.
- **`RetentionWindowCache.ConnectionTypes`** (Core, ClickHouse/Parquet collectors) gains `["Sqlite"] = "Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite"`.
  It builds its connection from the raw configured string, so it would open with `ReadWriteCreate`. The startup check (D6) has
  already confirmed the file exists, and the cache only reads. Still, it gets `Mode=ReadOnly` appended for SQLite through a small
  per-provider connection-string hook in that map, so it can never create a file. It fails open as today.
- **`ControlPlaneHealth` / `CollectorReadinessCheck`**: no change; a locked or missing file surfaces as a failed lookup like any outage.
- **Host defaults** (`appsettings.json`) stay `PostgreSQL`. The docs show the SQLite settings.

## Admin tool

- `SqliteAdminRepository : AdminRepositoryBase`, plus `case "Sqlite"` in `Program.cs` and `AdminProvider.Sqlite`; package reference
  `Microsoft.Data.Sqlite`. The tool keeps its rule of no reference to Core or any provider project. It carries its **own copy of the
  version constant and of the D6 check** (as `ApiKeyHashing` is a deliberate copy), plus a test asserting the copy equals
  `SqliteControlPlaneSchema.TargetVersion`.
- On startup it runs the D6 check and, on failure, prints the fix and exits: for a missing file, "start the API host once (or run
  `schema/apply-schema.sh controlplane sqlite <file>`)"; for an older version, "start the API host to migrate it"; for a newer one,
  "this Admin tool is older than the database; use a newer build". It never creates or migrates the database.
- Inserts use `RETURNING id`. Unique violation = `SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 }`
  (`SQLITE_CONSTRAINT_UNIQUE`). `expires_at` is written through the D7 format (the tool takes a `DateTimeOffset` today). `DescribeTarget`
  returns `("(local file)", <absolute path>)`. `CreatedAtColumnHeader` = "Created (UTC)", since the SQLite defaults are UTC, unlike MySQL's.

## `apply-schema.sh`

`controlplane sqlite <file>` (the file argument is required, like `telemetry parquet <root>`): fails if the directory is missing
(X7), skips when the version row already holds `CONTROLPLANE_TARGET_VERSION`, otherwise `sqlite3 <file> < schema/SQLite-ControlPlane.sql`
on a new file. It **creates only and never migrates**: an older version is refused with "start the API host to migrate it", and a newer
one is refused naming both versions. The migration logic exists once, in the library. `telemetry sqlite` is refused ("SQLite is a
control-plane provider only").

## Tests

- **`SqliteControlPlaneTests : ControlPlaneTestsBase`** (new `Provider=Sqlite` trait, no Docker): alert-rule CRUD, atomic
  `TryClaimFireAsync` (including the concurrent-claim case, which on SQLite exercises the busy wait), tenant catalog, retention
  settings, key touch, and `SqliteApiKeyLookupTests` (`expires_at` an hour either side of now, as the per-provider lookup tests do).
  A fixture creates a temp directory per collection and deletes it after.
- **Initializer and check** (no Docker): a fresh file is created with every table and the version; a second start changes nothing
  (row counts and `applied_at` unchanged); a newer recorded version fails naming both; an older one with no migration chain fails
  naming the missing step; a missing directory fails; two initializers started in parallel against one new file both succeed and leave
  one version row; the collector check fails on a missing file **without creating it**, on an empty file, and on an older and a newer
  version; `ApplySchema=false` makes the API fail on a missing file without creating it, and start on a current one; a relative path or
  `:memory:` fails registration.
- **Migration runner** (no Docker), against **test-only** migration scripts handed to the runner in place of the embedded set (a chain
  4.0.1 → 4.0.2 → 4.0.3 that adds a column, adds a table and rebuilds a table with a foreign key): a 4.0.1 file migrates through both
  steps and records both version rows; a 4.0.2 file runs only the last step; a gap in the chain fails before touching the file; two
  scripts with the same `<from>` fail; a failing second step rolls back to 4.0.1 with the first step's changes gone and the backup present
  and openable; a step that leaves a dangling foreign key fails at `foreign_key_check` and rolls back; two API instances migrating one
  file at once apply each step once; foreign keys are back on afterwards.
- **Migration script rules**: every embedded migration (none today) contains no `BEGIN`/`COMMIT`, `PRAGMA foreign_keys` or
  `control_plane_schema_version` insert, and its file name parses as `<from>-to-<to>` with `<to>` greater than `<from>`.
- **Fresh equals migrated**: for every version that has a migration chain to `TargetVersion`, build one database from the full create
  script and one from that version's historical create script plus the chain, and compare `sqlite_schema` (tables, columns, indexes,
  normalized SQL). It does nothing until the first migration exists; from then on, each schema change keeps the previous create script
  as a test fixture so the comparison has a starting point.
- **Timestamp format** (D7): `SqliteTime` round-trips; a column default and a .NET-written value of the same instant are equal as text;
  `ClaimFireSql` refuses inside the cooldown and claims after it, one second either side of the boundary.
- **Script parity**: a test parses `SQLite-ControlPlane.sql` and `PostgreSQL-ControlPlane.sql` and fails when the table/column sets,
  the length limits (`CHECK (length(..) <= n)` against `VARCHAR(n)`) or the defaults differ, and when the embedded resource differs
  from the file on disk.
- **`ControlPlaneRegistrationTests`**: `Sqlite` added to both sides (the missing-key failure).
- **`AUTOINCREMENT`** (X4): delete the highest tenant, create one, and its id is new.
- **Foreign keys** (X8): deleting a tenant cascades to its keys and rules even when the configured connection string says `Foreign Keys=False`.
- **Two processes**: a test that runs the collector-side lookup and touch in one process while another process (a small helper
  started with `dotnet`) updates retention settings and claims alerts in a loop for a few seconds. Neither side gets
  `SQLITE_BUSY` past the busy wait, and the final rows are what both sides wrote. This is the D2 promise, so it gets tested, not assumed.
- **Test infrastructure**: a `SqliteControlPlane` helper in `TestInfrastructure` (temp file, applies the schema through
  `SqliteControlPlaneSchema`, `InsertTenantAndApiKeyAsync` for `TenantSeeder`). `ParquetProviderContainer` uses it **by default** in
  place of its `PostgreSqlProviderContainer(controlPlaneOnly: true)` (D4), so `ControlPlaneProviderName` is `Sqlite` and a Parquet run
  starts no container. A constructor option keeps the PostgreSQL control plane available for the harness's `--control-plane postgresql`.
  `ParquetFixture` and `ParquetCompactedFixture` call `AddSqliteApiKeyLookup`/`...ControlPlaneManagement` in place of the
  PostgreSQL pair. `ClickHouseProviderContainer` keeps PostgreSQL.
- **Stress harness**: `--control-plane <postgresql|sqlite>`. The default depends on the telemetry provider: **Parquet → `sqlite`**,
  ClickHouse → `postgresql`, PostgreSQL/SQL Server/MySQL → their own database. Any provider may choose `sqlite` (D2). The SQLite file lives
  in the run's output directory, and `HostLauncher` passes `ControlPlane__Provider=Sqlite` and the absolute path. `result.json` records
  the control-plane provider (`controlPlane`), a scenario moved off its provider's default is tagged (`-cpsqlite`, `-cppostgresql`), and the read/write
  isolation pairing requires equal control planes. A result with no such field counts as PostgreSQL for Parquet and ClickHouse runs and as the
  telemetry provider otherwise, which is what it was, so Parquet runs recorded before this change are a different configuration from new default runs. `HostOrchestrationTests` gains a Parquet case asserting
  `ControlPlane__Provider=Sqlite` and an absolute path on both hosts. One recorded `smoke` run each on Parquet with both control planes,
  so the cost of key touches and alert claims on SQLite under load is known.

## Docs

- `src/Keryhe.Telemetry.Sqlite/README.md` (operator guide): configuration, the single-machine rule, absolute paths, the file and its
  `-wal`/`-shm` siblings, permissions (both service accounts need write access to the **directory**, since WAL creates files beside the
  database, which matters for `install-service.bat`), no network shares or Docker Desktop host bind mounts (as the Parquet README says
  for its root), backups (`sqlite3 <file> ".backup <copy>"` or `VACUUM INTO`, never a file copy while the hosts run), the startup order
  (API, then Admin, then collector) and the upgrade order (API first; it migrates and leaves a `.bak-` copy beside the file, which the
  operator deletes once satisfied), `ApplySchema=false` for migrating deliberately, and no downgrades.
- `docs/CONFIGURATION.md` (`ControlPlane:Provider=Sqlite`, `Telemetry:ControlPlane:Sqlite:ApplySchema`, the connection-string keys honored and the ones forced), `docs/SETUP.md` (a
  "no database server" path: Parquet + SQLite), `README.md`, the Parquet and Collector READMEs (the control plane no longer has to be
  relational-server), `CLAUDE.md` (provider list, control-plane providers, the commands block, the tests' Parquet-no-Docker note, the
  Admin tool's providers, and the schema-versioning paragraph: "there are no migration scripts" becomes "no migration scripts except
  the SQLite control plane's forward-only ones, which the API host applies", and a control-plane change also edits
  `SQLite-ControlPlane.sql` and adds a migration under `schema/SQLite-ControlPlane-Migrations/`).

## Order of work

1. Schema script + `SqliteTime` + the provider project with repositories, `SqliteControlPlaneSchema` (create, check and the migration
   runner), the initializer and check; host switches; `RetentionWindowCache`.
2. Tests for step 1 (everything above except the harness), `TestInfrastructure` helper, Parquet fixtures switched.
3. Admin tool.
4. `apply-schema.sh`.
5. Stress-harness option and the two recorded smoke runs.
6. Docs and `CLAUDE.md`.

Steps 3 and 4 depend only on step 1's schema and constants. Step 5 needs step 2's helper.

## Done when

`dotnet test tests/Keryhe.Telemetry.IntegrationTests --filter Provider=Sqlite` and `--filter Provider=Parquet` pass with Docker stopped;
the full suite passes; a Parquet + SQLite pair of hosts started from the README alone (API first, Admin creates a tenant and key, collector
second, the test data generator sends to it) shows data in the UI and fires an alert rule; and the two smoke runs are recorded.

## Not in scope

- SQLite as a telemetry provider.
- Moving the control plane between providers (no export/import tool; the Admin tool's create flow is the way to start over).
- Any change to the control-plane logical schema, and therefore any real migration script: this plan builds the mechanism only.
- Downgrades, and migrations for the PostgreSQL, SQL Server and MySQL control planes or for any telemetry schema.
- Migrating from the collector or the Admin tool.
