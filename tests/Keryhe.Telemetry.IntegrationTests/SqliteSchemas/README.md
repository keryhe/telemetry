# SQLite control-plane create scripts, per released version

`<version>.sql` is the full create script (`schema/SQLite-ControlPlane.sql`) as it was at that version. When a schema change
bumps the control-plane version, copy the *previous* create script here as `<previous version>.sql` and add the migration to
`schema/SQLite-ControlPlane-Migrations`. `SqliteMigrationTests.Fresh_equals_migrated` builds one database from this script plus the
migration chain and one from the current create script, and compares `sqlite_schema`.
