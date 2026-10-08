-- OpenTelemetry PostgreSQL control-plane schema -- control-plane schema 4.0.0
-- Tenants, API keys, alert rules and retention settings: the small, mutable, relational state that
-- sits beside the telemetry data (plans/control-plane-split.md). The telemetry tables are in
-- PostgreSQL-Telemetry.sql. No foreign key crosses the two, so they may share a database or live in two,
-- and either script may be applied first (control plane first is natural: a running collector needs
-- keys before it accepts data). Control-plane schema versions are recorded in
-- control_plane_schema_version.
--
-- A ClickHouse deployment has no control-plane script of its own: it runs this one on PostgreSQL,
-- SQL Server or MySQL (ControlPlane:Provider).
--
-- Schema 4.0.0 is a fresh-install schema: there is no upgrade path from 2.x or 3.x.
--
-- Usage:
--   psql -U postgres -d telemetry -f PostgreSQL-ControlPlane.sql

-- =============================================================================
-- TENANTS AND API KEYS
-- =============================================================================

CREATE TABLE tenants (
    "id"        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "name"      VARCHAR(255) NOT NULL,
    "created_at" TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT uk_tenant_name UNIQUE ("name")
);

CREATE TABLE api_keys (
    "id"         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "tenant_id"   BIGINT       NOT NULL REFERENCES tenants("id") ON DELETE CASCADE,
    "key_hash"    CHAR(64)     NOT NULL,
    "name"       VARCHAR(255) NOT NULL,
    "is_active"   BOOLEAN      NOT NULL DEFAULT TRUE,
    "created_at"  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "last_used_at" TIMESTAMPTZ,
    "expires_at"  TIMESTAMPTZ,
    CONSTRAINT uk_api_key_hash UNIQUE ("key_hash")
);
CREATE INDEX idx_api_keys_tenant_id ON api_keys ("tenant_id");

-- =============================================================================
-- SCHEMA VERSION TABLE
-- =============================================================================

CREATE TABLE control_plane_schema_version (
    "version"   VARCHAR(20) PRIMARY KEY,
    "applied_at" TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
-- NOTE: the control_plane_schema_version row is seeded at the very END of this script, so a partial/failed
-- apply never records a version that the apply-schema.sh version gate would wrongly treat as
-- "already applied".

-- =============================================================================
-- ALERTING TABLES
-- =============================================================================

CREATE TABLE alert_rules (
    "id"              INTEGER      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "tenant_id"        BIGINT       NOT NULL REFERENCES tenants("id") ON DELETE CASCADE,
    "name"            TEXT         NOT NULL,
    "type"            VARCHAR(50)  NOT NULL,
    "service_name"     VARCHAR(255),
    "condition_json"   JSONB        NOT NULL,
    "webhook_url"      TEXT         NOT NULL,
    "cooldown_minutes" INTEGER      NOT NULL DEFAULT 60,
    "enabled"         BOOLEAN      NOT NULL DEFAULT TRUE,
    "created_at"       TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "last_fired_at"     TIMESTAMPTZ
);
CREATE INDEX idx_alert_rules_tenant_id ON alert_rules ("tenant_id");
CREATE INDEX idx_alert_rules_tenant_enabled ON alert_rules ("tenant_id", "enabled") WHERE "enabled" = TRUE;

CREATE TABLE alert_events (
    "id"          BIGINT       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "rule_id"      INTEGER      NOT NULL,
    "fired_at"     TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "details_json" JSONB        NOT NULL,
    CONSTRAINT fk_alert_events_alert_rules FOREIGN KEY ("rule_id") REFERENCES alert_rules ("id") ON DELETE CASCADE
);
CREATE INDEX idx_alert_events_rule_id  ON alert_events ("rule_id");
CREATE INDEX idx_alert_events_fired_at ON alert_events ("fired_at" DESC);

-- =============================================================================
-- RETENTION TABLES
-- =============================================================================

-- Single global row (id = 1, enforced by the CHECK below) -- see IRetentionSettingsRepository for
-- why this is untenanted and why UPDATE, never INSERT, is the only mutation the app issues
-- against it after the seed row below.
CREATE TABLE retention_settings (
    "id"                    SMALLINT     PRIMARY KEY DEFAULT 1,
    "trace_retention_days"   INTEGER      NOT NULL,
    "log_retention_days"     INTEGER      NOT NULL,
    "metric_retention_days"  INTEGER      NOT NULL,
    "updated_at"             TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_retention_settings_singleton CHECK ("id" = 1)
);
INSERT INTO retention_settings ("id", "trace_retention_days", "log_retention_days", "metric_retention_days")
VALUES (1, 90, 90, 180)
ON CONFLICT ("id") DO NOTHING;

-- =============================================================================
-- SCHEMA VERSION (recorded LAST)
-- =============================================================================
-- Only reached when every statement above succeeded, so a partial apply cannot
-- leave a false version marker for the apply-schema.sh gate.
INSERT INTO control_plane_schema_version ("version") VALUES ('4.0.0')
ON CONFLICT ("version") DO UPDATE
SET "applied_at" = NOW();
