-- OpenTelemetry SQL Server Schema (SQL Server 2022) -- schema 3.0.0
-- Supports OTLP logs, metrics, and traces as defined in opentelemetry-proto.
--
-- Schema 3.0.0 is a fresh-install schema: there is no upgrade path from 2.x. See
-- plans/schema-simplification.md for what changed and why. In short: the hot tables (spans,
-- log_records, the five data-point tables) are append targets with no unique key and no foreign
-- keys, clustered on their access path instead of an IDENTITY, with only the indexes a named
-- query needs; tenant_id and service_name are columns on spans, log_records and metrics; and
-- there are no rollup tables, views or orphan tracking.
--
-- Differences from PostgreSQL-Schema.sql:
--   BIGINT GENERATED ALWAYS AS IDENTITY   ->  BIGINT IDENTITY(1,1)
--   VARCHAR(n)                            ->  NVARCHAR(n)
--   TEXT / JSONB                          ->  NVARCHAR(MAX)
--   DOUBLE PRECISION                      ->  FLOAT
--   BOOLEAN                               ->  BIT
--   TIMESTAMPTZ                           ->  DATETIME2
--   NOW()                                 ->  SYSDATETIME()
--   ON CONFLICT DO UPDATE ... RETURNING   ->  MERGE ... WITH (HOLDLOCK) ... OUTPUT
--
-- Usage:
--   sqlcmd -S <server> -d telemetry -i schema/SqlServer-Schema.sql

-- sqlcmd connects with SET QUOTED_IDENTIFIER OFF by default, but the filtered indexes require it
-- ON -- otherwise CREATE INDEX fails with Msg 1934 and, because schema_version would already be
-- committed above the failure, the apply-schema.sh version gate would wrongly report the schema
-- as applied. Set it (and ANSI_NULLS) ON for the whole session. (SSMS / ADO.NET already default
-- these ON.)
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- Row-versioned READ COMMITTED: readers neither block behind nor block ingestion's open
-- transactions, so the read repositories need no isolation-level plumbing of their own. A fresh
-- install has no other connections, so WITH ROLLBACK IMMEDIATE is safe. Reference-table
-- MERGE ... WITH (HOLDLOCK) stays serializable through the hint.
ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO

-- =============================================================================
-- COMMON TABLES (shared across signals)
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
    CONSTRAINT uk_api_key_hash UNIQUE (key_hash)
);
CREATE INDEX idx_api_keys_tenant_id ON api_keys (tenant_id);
GO

-- Resource represents the entity producing telemetry. A resource hash is not a resource identity
-- (two tenants running the same service share one), hence UNIQUE (tenant_id, resource_hash).
CREATE TABLE resources (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id       BIGINT        NOT NULL DEFAULT 1 REFERENCES tenants(id),
    resource_hash   CHAR(64)      NOT NULL,
    schema_url      NVARCHAR(2048),
    created_at      DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    attributes_json NVARCHAR(MAX),
    -- Extracted from attributes_json's "service.name" at upsert, and copied onto spans,
    -- log_records and metrics so hot reads filter on a column instead of joining here.
    service_name    NVARCHAR(255),
    CONSTRAINT uk_resource_tenant_hash UNIQUE (tenant_id, resource_hash)
);
CREATE INDEX idx_resources_tenant_service ON resources (tenant_id, service_name);
GO

-- Instrumentation scope (library). Shared across tenants on purpose: UNIQUE (scope_hash).
CREATE TABLE instrumentation_scopes (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    name            NVARCHAR(255) NOT NULL,
    version         NVARCHAR(255),
    schema_url      NVARCHAR(2048),
    scope_hash      CHAR(64)      NOT NULL,
    created_at      DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    attributes_json NVARCHAR(MAX),
    CONSTRAINT uk_scope_hash UNIQUE (scope_hash)
);
GO

-- =============================================================================
-- TRACES TABLES
-- =============================================================================

-- Trace spans. Events and links live in events_json/links_json on this row.
--
-- A plain append target: no unique key (a re-delivered span is stored twice and reads tolerate
-- it -- schema-simplification decision 7), no foreign keys, and clustered on the access path
-- (tenant_id, start_time_unix_nano, id) rather than on id, so concurrent flushes append at one
-- tail per tenant instead of all contending for the last page of an IDENTITY key. id stays a
-- BIGINT IDENTITY, only as the keyset-paging tiebreak and to make the clustered key unique.
--
-- trace_id/span_id are ANSI varchar with a binary collation so the sized AnsiString parameters
-- the application sends (IdParameter) match with no implicit conversion and the indexes seek.
CREATE TABLE spans (
    id                       BIGINT IDENTITY(1,1) NOT NULL,
    tenant_id                BIGINT        NOT NULL,
    service_name             NVARCHAR(255),
    trace_id                 VARCHAR(32)   COLLATE Latin1_General_BIN2 NOT NULL,
    span_id                  VARCHAR(16)   COLLATE Latin1_General_BIN2 NOT NULL,
    parent_span_id           VARCHAR(16)   COLLATE Latin1_General_BIN2,
    resource_id              BIGINT        NOT NULL,
    scope_id                 BIGINT        NOT NULL,
    name                     NVARCHAR(255) NOT NULL,
    kind                     NVARCHAR(20)  NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (kind IN ('UNSPECIFIED', 'INTERNAL', 'SERVER', 'CLIENT', 'PRODUCER', 'CONSUMER')),
    start_time_unix_nano     BIGINT        NOT NULL,
    end_time_unix_nano       BIGINT        NOT NULL,
    dropped_attributes_count INT           DEFAULT 0,
    dropped_events_count     INT           DEFAULT 0,
    dropped_links_count      INT           DEFAULT 0,
    trace_state              NVARCHAR(MAX),
    flags                    INT           DEFAULT 0,
    status_code              NVARCHAR(20)  NOT NULL DEFAULT 'UNSET'
        CHECK (status_code IN ('UNSET', 'OK', 'ERROR')),
    status_message           NVARCHAR(MAX),
    created_at               DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    attributes_json          NVARCHAR(MAX),
    events_json              NVARCHAR(MAX),
    links_json               NVARCHAR(MAX)
);
CREATE UNIQUE CLUSTERED INDEX cx_spans ON spans (tenant_id, start_time_unix_nano, id);
-- Trace detail, span by id, spans by parent within a trace, service-map parent join.
CREATE INDEX idx_spans_trace_span ON spans (trace_id, span_id);
-- Service-scoped anchors (the trace list's anchor is the selected service's earliest span).
CREATE INDEX idx_spans_tenant_service_time ON spans (tenant_id, service_name, start_time_unix_nano);
-- Errors mode. Filtered: error rows are rare, so this stays small.
CREATE INDEX idx_spans_error ON spans (tenant_id, start_time_unix_nano) WHERE status_code = 'ERROR';
GO

-- =============================================================================
-- METRICS TABLES
-- =============================================================================

-- Base metrics table. One row per (resource, scope, name, type); tenant_id and service_name are
-- copied from the resolved resource when the row is first written (cannot go stale: the
-- resource hash includes service.name, so a rename produces a new resource and new metrics rows).
CREATE TABLE metrics (
    id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id    BIGINT        NOT NULL,
    service_name NVARCHAR(255),
    resource_id  BIGINT        NOT NULL,
    scope_id     BIGINT        NOT NULL,
    name         NVARCHAR(255) NOT NULL,
    description  NVARCHAR(MAX),
    unit         NVARCHAR(63),
    -- [type] is bracketed because TYPE is a reserved word in T-SQL.
    [type]       NVARCHAR(30)  NOT NULL
        CHECK ([type] IN ('GAUGE', 'SUM', 'HISTOGRAM', 'EXPONENTIAL_HISTOGRAM', 'SUMMARY')),
    created_at   DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT fk_metrics_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_metrics_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id),
    -- Ingestion upsert key. [type] is part of it: the write path picks a data-point table from the
    -- incoming type while the read path picks from the stored type.
    -- Key bytes: 8 + 510 + 60 + 8 = 586, well inside the 1700-byte nonclustered limit.
    -- NOTE: under a case-insensitive database collation this treats metric names differing only
    -- by case as the same metric, which PostgreSQL and ClickHouse do not. Accepted.
    CONSTRAINT uk_metric_identity UNIQUE (resource_id, name, [type], scope_id)
);
-- Catalog (with or without a service filter) and by-name lookups.
-- Key bytes: 8 + 510 + 510 = 1028.
CREATE INDEX idx_metrics_tenant_service_name ON metrics (tenant_id, service_name, name);
GO

-- Data-point tables: no primary key, no foreign key, no unique key beyond the clustered key,
-- which is (metric_id, time_unix_nano, id) -- series reads and raw-point keyset paging.
-- exemplars_json holds the OTLP exemplar list (every data point except Summary).

CREATE TABLE gauge_data_points (
    id                   BIGINT IDENTITY(1,1) NOT NULL,
    metric_id            BIGINT NOT NULL,
    start_time_unix_nano BIGINT,
    time_unix_nano       BIGINT NOT NULL,
    value_double         FLOAT,
    value_int            BIGINT,
    flags                INT    DEFAULT 0,
    attributes_json      NVARCHAR(MAX),
    exemplars_json       NVARCHAR(MAX)
);
CREATE UNIQUE CLUSTERED INDEX cx_gauge_data_points ON gauge_data_points (metric_id, time_unix_nano, id);
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_gauge_time ON gauge_data_points (time_unix_nano);
GO

CREATE TABLE sum_data_points (
    id                      BIGINT IDENTITY(1,1) NOT NULL,
    metric_id               BIGINT       NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT       NOT NULL,
    value_double            FLOAT,
    value_int               BIGINT,
    aggregation_temporality NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    is_monotonic            BIT          DEFAULT 0,
    flags                   INT          DEFAULT 0,
    attributes_json         NVARCHAR(MAX),
    exemplars_json          NVARCHAR(MAX)
);
CREATE UNIQUE CLUSTERED INDEX cx_sum_data_points ON sum_data_points (metric_id, time_unix_nano, id);
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_sum_time ON sum_data_points (time_unix_nano);
GO

CREATE TABLE histogram_data_points (
    id                      BIGINT IDENTITY(1,1) NOT NULL,
    metric_id               BIGINT       NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT       NOT NULL,
    count                   BIGINT       NOT NULL,
    sum_value               FLOAT,
    bucket_counts           NVARCHAR(MAX),
    explicit_bounds         NVARCHAR(MAX),
    aggregation_temporality NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    flags                   INT          DEFAULT 0,
    min_value               FLOAT,
    max_value               FLOAT,
    attributes_json         NVARCHAR(MAX),
    exemplars_json          NVARCHAR(MAX)
);
CREATE UNIQUE CLUSTERED INDEX cx_histogram_data_points ON histogram_data_points (metric_id, time_unix_nano, id);
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_histogram_time ON histogram_data_points (time_unix_nano);
GO

CREATE TABLE exponential_histogram_data_points (
    id                      BIGINT IDENTITY(1,1) NOT NULL,
    metric_id               BIGINT       NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT       NOT NULL,
    count                   BIGINT       NOT NULL,
    sum_value               FLOAT,
    scale                   INT          NOT NULL,
    zero_count              BIGINT       NOT NULL,
    positive_offset         INT,
    positive_bucket_counts  NVARCHAR(MAX),
    negative_offset         INT,
    negative_bucket_counts  NVARCHAR(MAX),
    aggregation_temporality NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    flags                   INT          DEFAULT 0,
    min_value               FLOAT,
    max_value               FLOAT,
    attributes_json         NVARCHAR(MAX),
    exemplars_json          NVARCHAR(MAX)
);
CREATE UNIQUE CLUSTERED INDEX cx_exp_histogram_data_points ON exponential_histogram_data_points (metric_id, time_unix_nano, id);
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_exp_histogram_time ON exponential_histogram_data_points (time_unix_nano);
GO

CREATE TABLE summary_data_points (
    id                   BIGINT IDENTITY(1,1) NOT NULL,
    metric_id            BIGINT NOT NULL,
    start_time_unix_nano BIGINT,
    time_unix_nano       BIGINT NOT NULL,
    count                BIGINT NOT NULL,
    sum_value            FLOAT  NOT NULL,
    quantile_values      NVARCHAR(MAX),
    flags                INT    DEFAULT 0,
    attributes_json      NVARCHAR(MAX)
);
CREATE UNIQUE CLUSTERED INDEX cx_summary_data_points ON summary_data_points (metric_id, time_unix_nano, id);
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_summary_time ON summary_data_points (time_unix_nano);
GO

-- metric_last_seen: the metrics catalog's "has data in range" check reads this instead of scanning
-- the five data-point tables. No FK to metrics (a FK would make every touch lock-check the
-- metrics row, so ingestion's metrics MERGE and this table's touch worker could deadlock).
CREATE TABLE metric_last_seen (
    metric_id           BIGINT NOT NULL PRIMARY KEY,
    last_seen_unix_nano BIGINT NOT NULL
);
CREATE INDEX idx_metric_last_seen_last_seen ON metric_last_seen (last_seen_unix_nano);
GO

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

-- Log records. Same append-target shape as spans: no unique key, no foreign keys, clustered on
-- (tenant_id, time_unix_nano, id). Default 0 for time_unix_nano handles OTLP records where
-- TimeUnixNano is absent.
CREATE TABLE log_records (
    id                       BIGINT IDENTITY(1,1) NOT NULL,
    tenant_id                BIGINT       NOT NULL,
    service_name             NVARCHAR(255),
    resource_id              BIGINT       NOT NULL,
    scope_id                 BIGINT       NOT NULL,
    time_unix_nano           BIGINT       NOT NULL DEFAULT 0,
    observed_time_unix_nano  BIGINT,
    severity_number          INT,
    severity_text            NVARCHAR(MAX),
    event_name               NVARCHAR(256),
    body_type                NVARCHAR(20) DEFAULT 'STRING'
        CHECK (body_type IN ('STRING', 'BOOL', 'INT', 'DOUBLE', 'BYTES', 'ARRAY', 'KVLIST')),
    body_value               NVARCHAR(MAX),
    dropped_attributes_count INT          DEFAULT 0,
    flags                    INT          DEFAULT 0,
    trace_id                 VARCHAR(32)  COLLATE Latin1_General_BIN2,
    span_id                  VARCHAR(16)  COLLATE Latin1_General_BIN2,
    created_at               DATETIME2    NOT NULL DEFAULT SYSDATETIME(),
    attributes_json          NVARCHAR(MAX)
);
-- List paging, windows, search, per-tenant retention. The service filter is a residual predicate.
CREATE UNIQUE CLUSTERED INDEX cx_log_records ON log_records (tenant_id, time_unix_nano, id);
-- Logs for a trace.
CREATE INDEX idx_log_trace ON log_records (trace_id);
GO

-- =============================================================================
-- UTILITY TABLES
-- =============================================================================

CREATE TABLE schema_version (
    version    NVARCHAR(20) PRIMARY KEY,
    applied_at DATETIME2    NOT NULL DEFAULT SYSDATETIME()
);
GO
-- NOTE: the schema_version row is seeded at the very END of this script, so a partial/failed
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
MERGE schema_version AS target
USING (VALUES (N'3.0.0')) AS src (version)
ON target.version = src.version
WHEN MATCHED     THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) Row-versioned reads on:   SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME();
-- 2) List all indexes:         SELECT i.name, t.name FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id WHERE i.name IS NOT NULL ORDER BY t.name, i.name;
-- 3) Schema version:           SELECT * FROM schema_version;
