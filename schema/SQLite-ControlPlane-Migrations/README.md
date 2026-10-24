# SQLite control-plane migrations

Forward-only migrations for the SQLite control plane. The other providers have none (they are fresh-install only).
`Keryhe.Telemetry.Sqlite` embeds every `*.sql` file in this folder, and the API host applies the chain it needs at
startup (`SqliteControlPlaneSchema.EnsureCurrentAsync`); the collector and the Admin tool only check the version.

A control-plane schema change edits `../SQLite-ControlPlane.sql` (fresh installs keep running that one script) **and**
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
