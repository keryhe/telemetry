-- OpenTelemetry SQLite control-plane schema -- control-plane schema 4.0.1
-- Tenants (with their retention windows), API keys and alert rules: the small, mutable, relational state that
-- sits beside the telemetry data. SQLite is a control-plane provider only (never a Database:Provider): it lets a
-- Parquet deployment run with no database server. The tables, columns, defaults and indexes are those of
-- MySQL-ControlPlane.sql / PostgreSQL-ControlPlane.sql, translated:
--   * ids are INTEGER PRIMARY KEY AUTOINCREMENT: without AUTOINCREMENT SQLite reuses the highest rowid after a
--     delete, and telemetry rows carry tenant_id with no foreign key, so a reused tenant id would hand a deleted
--     tenant's data to a new tenant;
--   * tables are STRICT, and the length of a text column that has a VARCHAR(n) elsewhere is a CHECK, so the same
--     rows are valid on every provider;
--   * every timestamp is TEXT in UTC in exactly one layout, 'yyyy-MM-dd HH:mm:ss.fffffff', so a comparison in SQL
--     (the alert cooldown claim) is a plain text comparison. Keryhe.Telemetry.Sqlite's SqliteTime writes the same
--     layout.
--
-- Everything is idempotent (IF NOT EXISTS), so the API host applies this script at startup
-- (Keryhe.Telemetry.Sqlite embeds this file) and so can you. Later schema versions come as forward-only
-- migrations in schema/SQLite-ControlPlane-Migrations; the full script here always creates the current version.
--
-- Usage (needs the sqlite3 CLI; the directory must exist):
--   sqlite3 /var/lib/keryhe/control-plane.db < schema/SQLite-ControlPlane.sql
--   (or schema/apply-schema.sh controlplane sqlite /var/lib/keryhe/control-plane.db)

-- Stored in the file, so it holds for every later connection. The only statement that cannot sit in a transaction;
-- inside one it is a no-op, so the embedded copy runs the same text.
PRAGMA journal_mode = WAL;

-- =============================================================================
-- SCHEMA VERSION TABLE
-- =============================================================================

CREATE TABLE IF NOT EXISTS control_plane_schema_version (
    version    TEXT NOT NULL PRIMARY KEY CHECK (length(version) <= 20),
    applied_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f0000', 'now'))
) STRICT;

-- =============================================================================
-- TENANTS AND API KEYS
-- =============================================================================

CREATE TABLE IF NOT EXISTS tenants (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT    NOT NULL CHECK (length(name) <= 255),
    created_at TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f0000', 'now')),
    trace_retention_days  INTEGER NOT NULL DEFAULT 30,
    log_retention_days    INTEGER NOT NULL DEFAULT 30,
    metric_retention_days INTEGER NOT NULL DEFAULT 30,
    retention_updated_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f0000', 'now')),
    CONSTRAINT uk_tenant_name UNIQUE (name)
) STRICT;

CREATE TABLE IF NOT EXISTS api_keys (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    tenant_id    INTEGER NOT NULL,
    key_hash     TEXT    NOT NULL CHECK (length(key_hash) <= 64),
    name         TEXT    NOT NULL CHECK (length(name) <= 255),
    is_active    INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_at   TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f0000', 'now')),
    last_used_at TEXT,
    expires_at   TEXT, -- UTC, same layout as every timestamp here
    CONSTRAINT uk_api_key_hash UNIQUE (key_hash),
    CONSTRAINT fk_api_keys_tenants FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE CASCADE
) STRICT;
CREATE INDEX IF NOT EXISTS idx_api_keys_tenant_id ON api_keys (tenant_id);

-- =============================================================================
-- ALERTING TABLES
-- =============================================================================

CREATE TABLE IF NOT EXISTS alert_rules (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    tenant_id        INTEGER NOT NULL,
    name             TEXT    NOT NULL,
    type             TEXT    NOT NULL CHECK (length(type) <= 50),
    service_name     TEXT    CHECK (length(service_name) <= 255),
    condition_json   TEXT    NOT NULL CHECK (json_valid(condition_json)),
    webhook_url      TEXT    NOT NULL,
    cooldown_minutes INTEGER NOT NULL DEFAULT 60,
    enabled          INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    created_at       TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f0000', 'now')),
    last_fired_at    TEXT,
    CONSTRAINT fk_alert_rules_tenants FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE CASCADE
) STRICT;
CREATE INDEX IF NOT EXISTS idx_alert_rules_tenant_id      ON alert_rules (tenant_id);
-- A partial index covers the enabled-rules lookup (as on PostgreSQL).
CREATE INDEX IF NOT EXISTS idx_alert_rules_tenant_enabled ON alert_rules (tenant_id) WHERE enabled = 1;

CREATE TABLE IF NOT EXISTS alert_events (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_id      INTEGER NOT NULL,
    fired_at     TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f0000', 'now')),
    details_json TEXT    NOT NULL CHECK (json_valid(details_json)),
    CONSTRAINT fk_alert_events_alert_rules FOREIGN KEY (rule_id) REFERENCES alert_rules (id) ON DELETE CASCADE
) STRICT;
CREATE INDEX IF NOT EXISTS idx_alert_events_rule_id  ON alert_events (rule_id);
CREATE INDEX IF NOT EXISTS idx_alert_events_fired_at ON alert_events (fired_at DESC);

-- =============================================================================
-- SCHEMA VERSION (recorded LAST)
-- =============================================================================
-- Only inserted when every statement above succeeded, so a partial apply cannot leave a false version marker.
INSERT INTO control_plane_schema_version (version, applied_at)
VALUES ('4.0.1', strftime('%Y-%m-%d %H:%M:%f0000', 'now'))
ON CONFLICT (version) DO UPDATE SET applied_at = excluded.applied_at;

-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) List all tables
--    SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name;
--
-- 2) Verify schema version
--    SELECT * FROM control_plane_schema_version;
