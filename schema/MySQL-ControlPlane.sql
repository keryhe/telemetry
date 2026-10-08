-- OpenTelemetry MySQL control-plane schema -- control-plane schema 4.0.0
-- Tenants, API keys, alert rules and retention settings: the small, mutable, relational state that
-- sits beside the telemetry data (plans/control-plane-split.md). The telemetry tables are in
-- MySQL-Telemetry.sql. No foreign key crosses the two, so they may share a database or live in two,
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
--   mysql telemetry < schema/MySQL-ControlPlane.sql

-- =============================================================================
-- TENANTS AND API KEYS
-- =============================================================================

CREATE TABLE tenants (
    id         BIGINT AUTO_INCREMENT PRIMARY KEY,
    name       VARCHAR(255) NOT NULL,
    created_at DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT uk_tenant_name UNIQUE (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE api_keys (
    id           BIGINT AUTO_INCREMENT PRIMARY KEY,
    tenant_id    BIGINT       NOT NULL,
    key_hash     CHAR(64)     NOT NULL,
    name         VARCHAR(255) NOT NULL,
    is_active    TINYINT(1)   NOT NULL DEFAULT 1,
    created_at   DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    last_used_at DATETIME(6),
    expires_at   DATETIME(6) NULL, -- UTC by convention (UTC_TIMESTAMP(6)); read with SpecifyKind(Utc)
    CONSTRAINT uk_api_key_hash UNIQUE (key_hash),
    CONSTRAINT fk_api_keys_tenants FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_api_keys_tenant_id ON api_keys (tenant_id);

-- =============================================================================
-- SCHEMA VERSION TABLE
-- =============================================================================

CREATE TABLE control_plane_schema_version (
    version    VARCHAR(20) NOT NULL PRIMARY KEY,
    applied_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- =============================================================================
-- ALERTING TABLES
-- =============================================================================

CREATE TABLE alert_rules (
    id               INT          AUTO_INCREMENT PRIMARY KEY,
    tenant_id        BIGINT       NOT NULL,
    name             TEXT         NOT NULL,
    type             VARCHAR(50)  NOT NULL,
    service_name     VARCHAR(255),
    condition_json   JSON         NOT NULL,
    webhook_url      TEXT         NOT NULL,
    cooldown_minutes INT          NOT NULL DEFAULT 60,
    enabled          TINYINT(1)   NOT NULL DEFAULT 1,
    created_at       DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    last_fired_at    DATETIME(6),
    CONSTRAINT fk_alert_rules_tenants FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_alert_rules_tenant_id      ON alert_rules (tenant_id);
-- MySQL has no filtered indexes; a plain composite index covers the enabled-rules lookup.
CREATE INDEX idx_alert_rules_tenant_enabled ON alert_rules (tenant_id, enabled);

CREATE TABLE alert_events (
    id           BIGINT AUTO_INCREMENT PRIMARY KEY,
    rule_id      INT         NOT NULL,
    fired_at     DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    details_json JSON        NOT NULL,
    CONSTRAINT fk_alert_events_alert_rules FOREIGN KEY (rule_id) REFERENCES alert_rules (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_alert_events_rule_id  ON alert_events (rule_id);
CREATE INDEX idx_alert_events_fired_at ON alert_events (fired_at DESC);

-- =============================================================================
-- RETENTION TABLES
-- =============================================================================

-- Single global row (id = 1, enforced by the CHECK below) -- see IRetentionSettingsRepository for
-- why this is untenanted and why UPDATE, never INSERT, is the only mutation the app issues
-- against it after the seed row below.
CREATE TABLE retention_settings (
    id                    SMALLINT    NOT NULL PRIMARY KEY DEFAULT 1,
    trace_retention_days  INT         NOT NULL,
    log_retention_days    INT         NOT NULL,
    metric_retention_days INT         NOT NULL,
    updated_at            DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT chk_retention_settings_singleton CHECK (id = 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
INSERT INTO retention_settings (id, trace_retention_days, log_retention_days, metric_retention_days)
VALUES (1, 90, 90, 180);

-- =============================================================================
-- SCHEMA VERSION (recorded LAST)
-- =============================================================================
-- Only inserted when every statement above succeeded, so a partial apply cannot
-- leave a false version marker for the apply-schema.sh gate.
INSERT INTO control_plane_schema_version (version, applied_at)
VALUES ('4.0.0', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);

-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) List all base tables
--    SELECT table_name FROM information_schema.tables
--    WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' ORDER BY table_name;
--
-- 2) Verify schema version
--    SELECT * FROM control_plane_schema_version;
