# SQLite control-plane migrations

Forward-only migrations for the SQLite control plane, named `<from>-to-<to>.sql` (for example `4.0.1-to-4.0.2.sql`). They are
embedded in `Keryhe.Telemetry.Sqlite` and the API host applies them at startup. How to write one:
[Writing a migration](../../docs/SQLITE.md#writing-a-migration).
