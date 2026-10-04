-- OpenTelemetry MySQL Schema (MySQL 8.0+) -- schema 3.2.1
-- Supports OTLP logs, metrics, and traces as defined in opentelemetry-proto. MySQL 8 only: no
-- MariaDB compatibility is maintained.
--
-- Schema 3.0.0 is a fresh-install schema: there is no upgrade path from 2.x. See
-- plans/schema-simplification.md for what changed and why. In short: the hot tables (spans,
-- log_records, the five data-point tables) are append targets with no unique key and no foreign
-- keys, whose InnoDB primary key follows the access path instead of an AUTO_INCREMENT id, with
-- only the indexes a named query needs; tenant_id and service_name are columns on spans,
-- log_records and metrics; and there are no rollup tables, views or orphan tracking.
--
-- Differences from the SQL Server schema:
--   BIGINT IDENTITY(1,1)              ->  BIGINT AUTO_INCREMENT
--   NVARCHAR(n)                       ->  VARCHAR(n)
--   NVARCHAR(MAX) (text)              ->  TEXT / LONGTEXT
--   NVARCHAR(MAX) (JSON attributes)   ->  JSON  (native)
--   FLOAT                             ->  DOUBLE
--   BIT                               ->  TINYINT(1)
--   DATETIME2                         ->  DATETIME(6)
--   SYSDATETIME()                     ->  CURRENT_TIMESTAMP(6)
--   MERGE                             ->  INSERT ... ON DUPLICATE KEY UPDATE (runtime, in C#)
--   filtered index WHERE enabled = 1  ->  plain index (MySQL has no filtered indexes)
--   inline column REFERENCES          ->  table-level FOREIGN KEY (MySQL ignores inline refs)
--
-- Usage:
--   mysql telemetry < schema/MySQL-Schema.sql

-- =============================================================================
-- COMMON TABLES (shared across signals)
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

-- Resource represents the entity producing telemetry. A resource hash is not a resource identity
-- (two tenants running the same service share one), hence UNIQUE (tenant_id, resource_hash).
CREATE TABLE resources (
    id              BIGINT AUTO_INCREMENT PRIMARY KEY,
    tenant_id       BIGINT       NOT NULL DEFAULT 1,
    resource_hash   CHAR(64)     NOT NULL,
    schema_url      VARCHAR(2048),
    created_at      DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    attributes_json JSON,
    -- Extracted from attributes_json's "service.name" at upsert, and copied onto spans,
    -- log_records and metrics so hot reads filter on a column instead of joining here.
    service_name    VARCHAR(255),
    CONSTRAINT uk_resource_tenant_hash UNIQUE (tenant_id, resource_hash),
    CONSTRAINT fk_resources_tenants FOREIGN KEY (tenant_id) REFERENCES tenants (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_resources_tenant_service ON resources (tenant_id, service_name);

-- Instrumentation scope (library). Shared across tenants on purpose: UNIQUE (scope_hash).
CREATE TABLE instrumentation_scopes (
    id              BIGINT AUTO_INCREMENT PRIMARY KEY,
    name            VARCHAR(255) NOT NULL,
    version         VARCHAR(255),
    schema_url      VARCHAR(2048),
    scope_hash      CHAR(64)     NOT NULL,
    created_at      DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    attributes_json JSON,
    CONSTRAINT uk_scope_hash UNIQUE (scope_hash)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- =============================================================================
-- TRACES TABLES
-- =============================================================================

-- Trace spans. Events and links live in events_json/links_json on this row.
--
-- A plain append target: no unique key (a re-delivered span is stored twice and reads tolerate
-- it -- schema-simplification decision 7) and no foreign keys. InnoDB clusters on the primary
-- key, so it is (tenant_id, start_time_unix_nano, id): concurrent flushes append at one tail per
-- tenant instead of all contending for the last page of an AUTO_INCREMENT key. id stays
-- AUTO_INCREMENT as the keyset-paging tiebreak, and InnoDB requires an auto-increment column to
-- lead some index, hence KEY (id) (sequential, cheap).
--
-- trace_id/span_id are ascii_bin so lookups are exact, case-sensitive and use the index with the
-- connector's string parameters.
CREATE TABLE spans (
    id                       BIGINT       NOT NULL AUTO_INCREMENT,
    tenant_id                BIGINT       NOT NULL,
    service_name             VARCHAR(255),
    trace_id                 CHAR(32)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    span_id                  CHAR(16)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    parent_span_id           CHAR(16)     CHARACTER SET ascii COLLATE ascii_bin,
    resource_id              BIGINT       NOT NULL,
    scope_id                 BIGINT       NOT NULL,
    name                     VARCHAR(255) NOT NULL,
    kind                     VARCHAR(20)  NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (kind IN ('UNSPECIFIED', 'INTERNAL', 'SERVER', 'CLIENT', 'PRODUCER', 'CONSUMER')),
    start_time_unix_nano     BIGINT       NOT NULL,
    end_time_unix_nano       BIGINT       NOT NULL,
    dropped_attributes_count INT          DEFAULT 0,
    dropped_events_count     INT          DEFAULT 0,
    dropped_links_count      INT          DEFAULT 0,
    trace_state              TEXT,
    flags                    INT          DEFAULT 0,
    status_code              VARCHAR(20)  NOT NULL DEFAULT 'UNSET'
        CHECK (status_code IN ('UNSET', 'OK', 'ERROR')),
    status_message           TEXT,
    created_at               DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    attributes_json          JSON,
    events_json              JSON,
    links_json               JSON,
    PRIMARY KEY (tenant_id, start_time_unix_nano, id),
    KEY idx_spans_id (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
-- Trace detail, span by id, spans by parent within a trace, service-map parent join.
CREATE INDEX idx_spans_trace_span ON spans (trace_id, span_id);
-- Service-scoped anchors (the trace list's anchor is the selected service's earliest span).
CREATE INDEX idx_spans_tenant_service_time ON spans (tenant_id, service_name, start_time_unix_nano);
-- MySQL has no filtered index, so errors mode gets a plain composite that lets it seek straight to
-- the 'ERROR' slice. It costs an index entry on every insert; Phase 4 measures it and it is
-- dropped if material.
CREATE INDEX idx_spans_error ON spans (tenant_id, status_code, start_time_unix_nano);

-- =============================================================================
-- METRICS TABLES
-- =============================================================================

-- Base metrics table. One row per (resource, scope, name, type); tenant_id and service_name are
-- copied from the resolved resource when the row is first written (cannot go stale: the
-- resource hash includes service.name, so a rename produces a new resource and new metrics rows).
CREATE TABLE metrics (
    id           BIGINT AUTO_INCREMENT PRIMARY KEY,
    tenant_id    BIGINT       NOT NULL,
    service_name VARCHAR(255),
    resource_id  BIGINT       NOT NULL,
    scope_id     BIGINT       NOT NULL,
    name         VARCHAR(255) NOT NULL,
    description  TEXT,
    unit         VARCHAR(63),
    type         VARCHAR(30)  NOT NULL
        CHECK (type IN ('GAUGE', 'SUM', 'HISTOGRAM', 'EXPONENTIAL_HISTOGRAM', 'SUMMARY')),
    created_at   DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT fk_metrics_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_metrics_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id),
    -- Ingestion upsert key. type is part of it: the write path picks a data-point table from the
    -- incoming type while the read path picks from the stored type.
    -- Key bytes: 8 + 255*4 + 30*4 + 8 = 1156. Under the 3072-byte InnoDB limit for
    -- ROW_FORMAT=DYNAMIC, but OVER the 767-byte limit for COMPACT/REDUNDANT -- hence the explicit
    -- ROW_FORMAT below, so this cannot fail on a server whose innodb_default_row_format has been
    -- changed. Under a case-insensitive collation (utf8mb4_0900_ai_ci) metric names differing only
    -- by case are the same metric, which PostgreSQL and ClickHouse do not do. Accepted.
    CONSTRAINT uk_metric_identity UNIQUE (resource_id, name, type, scope_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 ROW_FORMAT=DYNAMIC;
-- Catalog (with or without a service filter) and by-name lookups. Key bytes: 8 + 1020 + 1020 = 2048.
CREATE INDEX idx_metrics_tenant_service_name ON metrics (tenant_id, service_name, name);

-- Data-point tables: no foreign key, no unique key beyond the primary key, which is
-- (metric_id, time_unix_nano, id) -- series reads and raw-point keyset paging. id stays
-- AUTO_INCREMENT behind KEY (id). exemplars_json holds the OTLP exemplar list (every data point
-- except Summary).

CREATE TABLE gauge_data_points (
    id                   BIGINT NOT NULL AUTO_INCREMENT,
    metric_id            BIGINT NOT NULL,
    start_time_unix_nano BIGINT,
    time_unix_nano       BIGINT NOT NULL,
    value_double         DOUBLE,
    value_int            BIGINT,
    flags                INT    DEFAULT 0,
    attributes_json      JSON,
    exemplars_json       JSON,
    PRIMARY KEY (metric_id, time_unix_nano, id),
    KEY idx_gauge_id (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_gauge_time ON gauge_data_points (time_unix_nano);

CREATE TABLE sum_data_points (
    id                      BIGINT NOT NULL AUTO_INCREMENT,
    metric_id               BIGINT       NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT       NOT NULL,
    value_double            DOUBLE,
    value_int               BIGINT,
    aggregation_temporality VARCHAR(20)  NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    is_monotonic            TINYINT(1)   DEFAULT 0,
    flags                   INT          DEFAULT 0,
    attributes_json         JSON,
    exemplars_json          JSON,
    PRIMARY KEY (metric_id, time_unix_nano, id),
    KEY idx_sum_id (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_sum_time ON sum_data_points (time_unix_nano);

CREATE TABLE histogram_data_points (
    id                      BIGINT NOT NULL AUTO_INCREMENT,
    metric_id               BIGINT       NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT       NOT NULL,
    count                   BIGINT       NOT NULL,
    sum_value               DOUBLE,
    bucket_counts           JSON,
    explicit_bounds         JSON,
    aggregation_temporality VARCHAR(20)  NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    flags                   INT          DEFAULT 0,
    min_value               DOUBLE,
    max_value               DOUBLE,
    attributes_json         JSON,
    exemplars_json          JSON,
    PRIMARY KEY (metric_id, time_unix_nano, id),
    KEY idx_histogram_id (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_histogram_time ON histogram_data_points (time_unix_nano);

CREATE TABLE exponential_histogram_data_points (
    id                      BIGINT NOT NULL AUTO_INCREMENT,
    metric_id               BIGINT       NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT       NOT NULL,
    count                   BIGINT       NOT NULL,
    sum_value               DOUBLE,
    scale                   INT          NOT NULL,
    zero_count              BIGINT       NOT NULL,
    positive_offset         INT,
    positive_bucket_counts  JSON,
    negative_offset         INT,
    negative_bucket_counts  JSON,
    aggregation_temporality VARCHAR(20)  NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    flags                   INT          DEFAULT 0,
    min_value               DOUBLE,
    max_value               DOUBLE,
    attributes_json         JSON,
    exemplars_json          JSON,
    PRIMARY KEY (metric_id, time_unix_nano, id),
    KEY idx_exp_histogram_id (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_exp_histogram_time ON exponential_histogram_data_points (time_unix_nano);

CREATE TABLE summary_data_points (
    id                   BIGINT NOT NULL AUTO_INCREMENT,
    metric_id            BIGINT NOT NULL,
    start_time_unix_nano BIGINT,
    time_unix_nano       BIGINT NOT NULL,
    count                BIGINT NOT NULL,
    sum_value            DOUBLE NOT NULL,
    quantile_values      JSON,
    flags                INT    DEFAULT 0,
    attributes_json      JSON,
    PRIMARY KEY (metric_id, time_unix_nano, id),
    KEY idx_summary_id (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_summary_time ON summary_data_points (time_unix_nano);

-- metric_last_seen: the metrics catalog's "has data in range" check reads this instead of scanning
-- the five data-point tables. No FK to metrics (a FK would make every touch lock-check the
-- metrics row, so ingestion's metrics upsert and this table's touch worker could deadlock).
CREATE TABLE metric_last_seen (
    metric_id           BIGINT NOT NULL PRIMARY KEY,
    last_seen_unix_nano BIGINT NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_metric_last_seen_last_seen ON metric_last_seen (last_seen_unix_nano);

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

-- Log records. Same append-target shape as spans: no unique key, no foreign keys, primary key
-- (tenant_id, time_unix_nano, id) behind KEY (id). Default 0 for time_unix_nano handles OTLP
-- records where TimeUnixNano is absent.
CREATE TABLE log_records (
    id                       BIGINT      NOT NULL AUTO_INCREMENT,
    tenant_id                BIGINT      NOT NULL,
    service_name             VARCHAR(255),
    resource_id              BIGINT      NOT NULL,
    scope_id                 BIGINT      NOT NULL,
    time_unix_nano           BIGINT      NOT NULL DEFAULT 0,
    observed_time_unix_nano  BIGINT,
    severity_number          INT,
    severity_text            VARCHAR(255),
    event_name               VARCHAR(256),
    body_type                VARCHAR(20) DEFAULT 'STRING'
        CHECK (body_type IN ('STRING', 'BOOL', 'INT', 'DOUBLE', 'BYTES', 'ARRAY', 'KVLIST')),
    body_value               LONGTEXT,
    dropped_attributes_count INT         DEFAULT 0,
    flags                    INT         DEFAULT 0,
    trace_id                 CHAR(32)    CHARACTER SET ascii COLLATE ascii_bin,
    span_id                  CHAR(16)    CHARACTER SET ascii COLLATE ascii_bin,
    created_at               DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    attributes_json          JSON,
    PRIMARY KEY (tenant_id, time_unix_nano, id),
    KEY idx_log_id (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
-- Logs for a trace. The service filter is a residual predicate on the primary key.
CREATE INDEX idx_log_trace ON log_records (trace_id);

-- =============================================================================
-- SUMMARY ROLLUPS (schema 3.2.0; plans/summary-rollups.md)
-- =============================================================================

-- Per-minute rollups the dashboard, trace list and logs page read instead of scanning spans and
-- log records. request_rollup_minute counts INBOUND spans (kind SERVER or CONSUMER) per
-- (tenant, service, minute of start time): request_count, error_count, duration sum/max and a
-- 24-band doubling duration histogram (h00 = under 0.25 ms, hNN = [0.25 ms * 2^(NN-1), 0.25 ms * 2^NN),
-- h23 = 1,048.6 s and over). log_rollup_minute counts log records per (tenant, service, severity
-- number, minute); severity_number is -1 for a record whose severity is NULL. Rows are partial:
-- no unique key, no foreign keys, reads SUM them. service_name is '' for a span or log without one.
CREATE TABLE request_rollup_minute (
    id                     BIGINT       NOT NULL AUTO_INCREMENT,
    tenant_id              BIGINT       NOT NULL,
    service_name           VARCHAR(255) NOT NULL DEFAULT '',
    bucket_start_unix_nano BIGINT       NOT NULL,
    request_count          BIGINT       NOT NULL,
    error_count            BIGINT       NOT NULL,
    sum_duration_nanos     BIGINT       NOT NULL,
    max_duration_nanos     BIGINT       NOT NULL,
    h00 BIGINT NOT NULL DEFAULT 0,
    h01 BIGINT NOT NULL DEFAULT 0,
    h02 BIGINT NOT NULL DEFAULT 0,
    h03 BIGINT NOT NULL DEFAULT 0,
    h04 BIGINT NOT NULL DEFAULT 0,
    h05 BIGINT NOT NULL DEFAULT 0,
    h06 BIGINT NOT NULL DEFAULT 0,
    h07 BIGINT NOT NULL DEFAULT 0,
    h08 BIGINT NOT NULL DEFAULT 0,
    h09 BIGINT NOT NULL DEFAULT 0,
    h10 BIGINT NOT NULL DEFAULT 0,
    h11 BIGINT NOT NULL DEFAULT 0,
    h12 BIGINT NOT NULL DEFAULT 0,
    h13 BIGINT NOT NULL DEFAULT 0,
    h14 BIGINT NOT NULL DEFAULT 0,
    h15 BIGINT NOT NULL DEFAULT 0,
    h16 BIGINT NOT NULL DEFAULT 0,
    h17 BIGINT NOT NULL DEFAULT 0,
    h18 BIGINT NOT NULL DEFAULT 0,
    h19 BIGINT NOT NULL DEFAULT 0,
    h20 BIGINT NOT NULL DEFAULT 0,
    h21 BIGINT NOT NULL DEFAULT 0,
    h22 BIGINT NOT NULL DEFAULT 0,
    h23 BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, bucket_start_unix_nano, id),
    KEY idx_request_rollup_id (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE log_rollup_minute (
    id                     BIGINT       NOT NULL AUTO_INCREMENT,
    tenant_id              BIGINT       NOT NULL,
    service_name           VARCHAR(255) NOT NULL DEFAULT '',
    severity_number        INT          NOT NULL,
    bucket_start_unix_nano BIGINT       NOT NULL,
    record_count           BIGINT       NOT NULL,
    PRIMARY KEY (tenant_id, bucket_start_unix_nano, id),
    KEY idx_log_rollup_id (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- HOUR TIER (schema 3.2.1, MySQL only: the measurement gate in plans/summary-rollups.md found a week of
-- minute rows too slow to sum on MySQL, 2.3 s p95 against a 1 s budget; the other providers met it and
-- ship no hour tier). One row per (tenant, service, hour) -- request_rollup_hour -- and per (tenant,
-- service, severity, hour) -- log_rollup_hour -- rebuilt from the minute rows by the API host's
-- RollupCompactionWorker. rollup_compaction records, per signal, the hour boundary below which the hour
-- tier is authoritative; reads of a bucket an hour or wider use the hour tier before it and the minute
-- rows after it. The secondary indexes serve the compaction's delete-by-hour.
CREATE TABLE request_rollup_hour (
    tenant_id              BIGINT       NOT NULL,
    service_name           VARCHAR(255) NOT NULL DEFAULT '',
    bucket_start_unix_nano BIGINT       NOT NULL,
    request_count          BIGINT       NOT NULL,
    error_count            BIGINT       NOT NULL,
    sum_duration_nanos     BIGINT       NOT NULL,
    max_duration_nanos     BIGINT       NOT NULL,
    h00 BIGINT NOT NULL DEFAULT 0,
    h01 BIGINT NOT NULL DEFAULT 0,
    h02 BIGINT NOT NULL DEFAULT 0,
    h03 BIGINT NOT NULL DEFAULT 0,
    h04 BIGINT NOT NULL DEFAULT 0,
    h05 BIGINT NOT NULL DEFAULT 0,
    h06 BIGINT NOT NULL DEFAULT 0,
    h07 BIGINT NOT NULL DEFAULT 0,
    h08 BIGINT NOT NULL DEFAULT 0,
    h09 BIGINT NOT NULL DEFAULT 0,
    h10 BIGINT NOT NULL DEFAULT 0,
    h11 BIGINT NOT NULL DEFAULT 0,
    h12 BIGINT NOT NULL DEFAULT 0,
    h13 BIGINT NOT NULL DEFAULT 0,
    h14 BIGINT NOT NULL DEFAULT 0,
    h15 BIGINT NOT NULL DEFAULT 0,
    h16 BIGINT NOT NULL DEFAULT 0,
    h17 BIGINT NOT NULL DEFAULT 0,
    h18 BIGINT NOT NULL DEFAULT 0,
    h19 BIGINT NOT NULL DEFAULT 0,
    h20 BIGINT NOT NULL DEFAULT 0,
    h21 BIGINT NOT NULL DEFAULT 0,
    h22 BIGINT NOT NULL DEFAULT 0,
    h23 BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, bucket_start_unix_nano, service_name),
    KEY idx_request_hour_bucket (bucket_start_unix_nano)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE log_rollup_hour (
    tenant_id              BIGINT       NOT NULL,
    service_name           VARCHAR(255) NOT NULL DEFAULT '',
    severity_number        INT          NOT NULL,
    bucket_start_unix_nano BIGINT       NOT NULL,
    record_count           BIGINT       NOT NULL,
    PRIMARY KEY (tenant_id, bucket_start_unix_nano, service_name, severity_number),
    KEY idx_log_hour_bucket (bucket_start_unix_nano)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE rollup_compaction (
    signal_kind                 VARCHAR(16) NOT NULL PRIMARY KEY,
    compacted_through_unix_nano BIGINT      NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- =============================================================================
-- UTILITY TABLES
-- =============================================================================

CREATE TABLE schema_version (
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
INSERT INTO schema_version (version, applied_at)
VALUES ('3.2.1', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);

-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) List all base tables
--    SELECT table_name FROM information_schema.tables
--    WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' ORDER BY table_name;
--
-- 2) Verify schema version
--    SELECT * FROM schema_version;
