# SQLite control plane

Rules, behaviour, migrations and backups of the SQLite control plane ([`Keryhe.Telemetry.Sqlite`](../src/Keryhe.Telemetry.Sqlite/README.md)).

## The rules that matter

- **One machine, a local file system.** The collector, the API host and the Admin tool all open the same file, so they run on one
  machine and its local disk (a container volume shared by containers on one Linux host is fine). A network share, and Docker
  Desktop's host bind mounts, give stale or locked databases. This rules out load-balanced hosts on separate machines; use PostgreSQL,
  SQL Server or MySQL for those.
- **An absolute path.** The collector and the API have different working directories, so a relative path (or `:memory:`) would open
  different databases without any error; registration refuses both. Use the *same* path in every host's configuration and in
  `ConnectionStrings:Admin`.
- **Write access to the directory**, for every service account that runs a host (including `install-service.bat` installs): WAL mode
  creates `control-plane.db-wal` and `control-plane.db-shm` beside the file. The hosts create the file and, when it is missing, its
  directory; each logs the absolute path it created, so check that both hosts name the same one (a mistyped path gives a host its own empty database).
- **Either host can start first.** At startup the collector and the API host each create the database if it has no schema and apply any
  later forward-only migrations (below), before anything else starts (the collector before it binds a port). Two hosts starting together
  take turns on the file's write lock, so the work is done once. With a fixed tenant (a `FixedTenant` collector's `FixedTenantId`, the API
  host's `TelemetryUi:FixedTenantId`) the same step creates that tenant, named `Default`, when it has no row. The **Admin tool** never creates
  or migrates: it fails when the file is missing or not at exactly its build's version, so start a host before using it.
- **No downgrades.** A database newer than the build is refused, naming both versions.

## What the provider does for you

- Opens every connection with `Foreign Keys=True` and `Pooling=True` whatever the configured string says (`ON DELETE CASCADE` needs foreign
  keys on, which SQLite turns off per connection), and a 30 s busy wait (`Default Timeout`) unless the string sets one: writers (key touch,
  alert claim, retention settings, the Admin tool) take turns on the file's write lock and wait rather than fail.
- Stores every timestamp as UTC text in one layout (`yyyy-MM-dd HH:mm:ss.fffffff`), so the alert cooldown claim is a plain text comparison.
- Uses `INTEGER PRIMARY KEY AUTOINCREMENT`: a deleted tenant's id is never reused (telemetry rows carry `tenant_id` with no foreign key, so a
  reused id would hand a deleted tenant's data to a new tenant).
- Uses `STRICT` tables with `CHECK`s for the lengths and JSON the other providers' types enforce.
- Journal mode is WAL (stored in the file), so the collector's reads run while the API writes.

## Creating the file ahead of time

Either host does it for you. To create it yourself (needs the `sqlite3` CLI; creates a missing directory; fresh files only):

```bash
schema/apply-schema.sh controlplane sqlite /var/lib/keryhe/control-plane.db
```

With `Telemetry:ControlPlane:Sqlite:ApplySchema=false` the API host only checks, like the collector, for an operator who wants to create or
migrate deliberately (for example after taking a backup of their own, or from a deployment step calling
`SqliteControlPlaneSchema.EnsureCurrentAsync`).

## Migrations

The control-plane schema is versioned as a whole (`control_plane_schema_version`; the same version on all four providers). The other
providers are fresh-install only. SQLite is the exception: a schema change adds a **forward-only migration**
(`schema/SQLite-ControlPlane-Migrations/<from>-to-<to>.sql`, embedded in this assembly) beside the edit to the full create script, which
fresh installs keep running. There are none yet. When the API host finds an older version it:

1. copies the file with `VACUUM INTO` to `<file>.bak-<from>-<UTC time>-<id>` (delete it once you are satisfied; a failed migration keeps it),
2. switches foreign keys off, takes the write lock (`BEGIN IMMEDIATE`, so two API processes starting together serialize) and re-reads the
   version,
3. runs each migration in order, recording each version row,
4. runs `PRAGMA foreign_key_check`, then commits and switches foreign keys back on.

Any failure rolls everything back (SQLite DDL is transactional), leaves the file at its old version and names the step. An older version with
no complete chain of migrations is refused. See [Writing a migration](#writing-a-migration).

## Backups

Never copy the file while the hosts run (the `-wal` file holds recent writes). Use the `sqlite3` CLI: `sqlite3 control-plane.db ".backup copy.db"`,
or `VACUUM INTO 'copy.db'` from any SQLite client.

## Registration

```csharp
// The collector: the key lookup, the last-used update, and the startup step that creates / migrates the file (ApplySchema). The optional
// second argument is a fixed tenant to create, named Default, when it has no row.
services.AddSqliteApiKeyLookup(configuration, fixedTenantId: null);
// The API host: alert rules, the tenant catalog, retention settings, and the same startup step.
services.AddSqliteControlPlaneManagement(configuration, fixedTenantId: null);
```

Both read `ConnectionStrings:ControlPlane` at registration and fail naming the key when it is missing, blank, relative or `:memory:`.
`SqliteControlPlaneSchema` (`TargetVersion`, `EnsureCurrentAsync`, `CheckAsync`) is public, so a host that is not either of these can create,
migrate or check the file itself.

## Writing a migration

A control-plane schema change edits `schema/SQLite-ControlPlane.sql` (fresh installs keep running that one script) **and**
adds a migration from the previous version here:

* Name: `<from>-to-<to>.sql`, e.g. `4.0.1-to-4.0.2.sql`. `<to>` must be greater than `<from>`, each `<from>` appears once,
  and the chain must reach the current schema version without a gap.
* Content: plain DDL/DML only. **No** `BEGIN`/`COMMIT`/`ROLLBACK`, **no** `PRAGMA foreign_keys` and **no**
  `control_plane_schema_version` insert: the runner owns the transaction (`BEGIN IMMEDIATE`), turns foreign keys off around
  it, runs `PRAGMA foreign_key_check` before committing and records the version row. A test enforces this.
* `ALTER TABLE` can only add, rename or drop a column. For anything else (a type, a constraint, a foreign key) use SQLite's
  table rebuild: create the new table, `INSERT ... SELECT` the rows, drop the old table, rename the new one, recreate its indexes.
* A test builds a database from the previous version's create script plus the chain and from the current create script and
  compares `sqlite_schema`: keep the previous create script as a test fixture (`tests/.../SqliteSchemas/<version>.sql`).
