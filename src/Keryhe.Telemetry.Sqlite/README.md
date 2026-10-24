# Keryhe.Telemetry.Sqlite

The **SQLite control plane**: tenants (with their retention windows), API keys, alert rules and alert events in one SQLite file, so a
Parquet deployment needs **no database server at all**. It is a control-plane provider only (`ControlPlane:Provider=Sqlite`), never a
`Database:Provider`, and works beside any telemetry provider.

```
ControlPlane:Provider                      = Sqlite
ConnectionStrings:ControlPlane             = Data Source=/var/lib/keryhe/control-plane.db
Telemetry:ControlPlane:Sqlite:ApplySchema  = true   (API host only; the default)
```

## The rules that matter

- **One machine, a local file system.** The collector, the API host and the Admin tool all open the same file, so they run on one
  machine and its local disk (a container volume shared by containers on one Linux host is fine). A network share, and Docker
  Desktop's host bind mounts, give stale or locked databases. This rules out load-balanced hosts on separate machines; use PostgreSQL,
  SQL Server or MySQL for those.
- **An absolute path.** The collector and the API have different working directories, so a relative path (or `:memory:`) would open
  different databases without any error; registration refuses both. Use the *same* path in every host's configuration and in
  `ConnectionStrings:Admin`.
- **Write access to the directory**, for every service account that runs a host (including `install-service.bat` installs): WAL mode
  creates `control-plane.db-wal` and `control-plane.db-shm` beside the file. The host creates the *file*, never its *directory*; a
  missing directory fails startup, so a mistyped path cannot split the hosts onto two databases.
- **The API host owns the schema; start it first.** At startup it creates the database if it has no schema and applies any later
  forward-only migrations (below), before its alerting and retention workers start. The **collector** and the **Admin tool** never
  create or migrate: they fail when the file is missing or not at exactly this build's version (the collector before it binds a port, with
  a message that says to start the API host). So upgrade the API host first, then the others.
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

The API host does it for you. To create it yourself (needs the `sqlite3` CLI; the directory must exist; fresh files only):

```bash
mkdir -p /var/lib/keryhe
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
no complete chain of migrations is refused. See `schema/SQLite-ControlPlane-Migrations/README.md` for how to write one.

## Backups

Never copy the file while the hosts run (the `-wal` file holds recent writes). Use the `sqlite3` CLI: `sqlite3 control-plane.db ".backup copy.db"`,
or `VACUUM INTO 'copy.db'` from any SQLite client.

## Registration

```csharp
// A host that authenticates by API key (the collector): the key lookup, the last-used update, and a startup check that exits if the file is
// not there / not current. It never creates or migrates.
services.AddSqliteApiKeyLookup(configuration);
// The host that owns the schema (the API host): alert rules, the tenant catalog, retention settings, and the startup step that creates /
// migrates the file. A host that is neither can call either, or SqliteControlPlaneSchema directly.
services.AddSqliteControlPlaneManagement(configuration);
```

Both read `ConnectionStrings:ControlPlane` at registration and fail naming the key when it is missing, blank, relative or `:memory:`.
`SqliteControlPlaneSchema` (`TargetVersion`, `EnsureCurrentAsync`, `CheckAsync`) is public, so a host that is not either of these can create,
migrate or check the file itself.
