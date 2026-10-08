-- OpenTelemetry SQL Server control-plane schema -- control-plane schema 4.0.0
-- Tenants, API keys, alert rules and retention settings: the small, mutable, relational state that
-- sits beside the telemetry data (plans/control-plane-split.md). The telemetry tables are in
-- SqlServer-Telemetry.sql. No foreign key crosses the two, so they may share a database or live in two,
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
--   sqlcmd -S <server> -d telemetry -i schema/SqlServer-ControlPlane.sql

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- Row-versioned READ COMMITTED, as in the telemetry script (idempotent, so harmless when both scripts
-- share a database, and correct when they do not). A fresh install has no other connections, so WITH
-- ROLLBACK IMMEDIATE is safe.
ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO

-- =============================================================================
-- TENANTS AND API KEYS
-- =============================================================================

CREATE TABLE tenants (
    id         BIGINT IDENTITY(1,1) PRIMARY KEY,
    name       NVARCHAR(255) NOT NULL,
    created_at DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT uk_tenant_name UNIQUE (name)
);

CREATE TABLE api_keys (
    id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id    BIGINT        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    key_hash     CHAR(64)      NOT NULL,
    name         NVARCHAR(255) NOT NULL,
    is_active    BIT           NOT NULL DEFAULT 1,
    created_at   DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    last_used_at DATETIME2,
    expires_at   DATETIMEOFFSET(7) NULL, -- UTC (written with SYSUTCDATETIME()/an offset); NOT DATETIME2, see collector-authentication decision 12
    CONSTRAINT uk_api_key_hash UNIQUE (key_hash)
);
CREATE INDEX idx_api_keys_tenant_id ON api_keys (tenant_id);
GO

-- =============================================================================
-- SCHEMA VERSION TABLE
-- =============================================================================

CREATE TABLE control_plane_schema_version (
    version    NVARCHAR(20) PRIMARY KEY,
    applied_at DATETIME2    NOT NULL DEFAULT SYSDATETIME()
);
GO
-- NOTE: the control_plane_schema_version row is seeded at the very END of this script, so a partial/failed
-- apply never records a version that the apply-schema.sh version gate would wrongly treat as
-- "already applied".

-- =============================================================================
-- ALERTING TABLES
-- =============================================================================

CREATE TABLE alert_rules (
    id               INT           IDENTITY(1,1) PRIMARY KEY,
    tenant_id        BIGINT        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name             NVARCHAR(MAX) NOT NULL,
    -- [type] bracketed because TYPE is a reserved word in T-SQL.
    [type]           NVARCHAR(50)  NOT NULL,
    service_name     NVARCHAR(255),
    condition_json   NVARCHAR(MAX) NOT NULL,
    webhook_url      NVARCHAR(MAX) NOT NULL,
    cooldown_minutes INT           NOT NULL DEFAULT 60,
    enabled          BIT           NOT NULL DEFAULT 1,
    created_at       DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    last_fired_at    DATETIME2
);
CREATE INDEX idx_alert_rules_tenant_id      ON alert_rules (tenant_id);
-- SQL Server filtered index: WHERE enabled = 1  (BIT 1 = TRUE)
CREATE INDEX idx_alert_rules_tenant_enabled ON alert_rules (tenant_id, enabled) WHERE enabled = 1;
GO

CREATE TABLE alert_events (
    id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    rule_id      INT           NOT NULL,
    fired_at     DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    details_json NVARCHAR(MAX) NOT NULL,
    CONSTRAINT fk_alert_events_alert_rules FOREIGN KEY (rule_id) REFERENCES alert_rules (id) ON DELETE CASCADE
);
CREATE INDEX idx_alert_events_rule_id  ON alert_events (rule_id);
CREATE INDEX idx_alert_events_fired_at ON alert_events (fired_at DESC);
GO

-- =============================================================================
-- RETENTION TABLES
-- =============================================================================

-- Single global row (id = 1, enforced by the CHECK below) -- see IRetentionSettingsRepository for
-- why this is untenanted and why UPDATE, never INSERT, is the only mutation the app issues
-- against it after the seed row below.
CREATE TABLE retention_settings (
    id                    SMALLINT  NOT NULL PRIMARY KEY DEFAULT 1,
    trace_retention_days  INT       NOT NULL,
    log_retention_days    INT       NOT NULL,
    metric_retention_days INT       NOT NULL,
    updated_at            DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT chk_retention_settings_singleton CHECK (id = 1)
);
GO
INSERT INTO retention_settings (id, trace_retention_days, log_retention_days, metric_retention_days)
VALUES (1, 90, 90, 180);
GO

-- =============================================================================
-- SCHEMA VERSION (recorded LAST)
-- =============================================================================
-- Only reached when every statement above succeeded, so a partial apply cannot
-- leave a false version marker for the apply-schema.sh gate.
MERGE control_plane_schema_version AS target
USING (VALUES (N'4.0.0')) AS src (version)
ON target.version = src.version
WHEN MATCHED     THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) Row-versioned reads on:   SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME();
-- 2) List all indexes:         SELECT i.name, t.name FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id WHERE i.name IS NOT NULL ORDER BY t.name, i.name;
-- 3) Schema version:           SELECT * FROM control_plane_schema_version;
