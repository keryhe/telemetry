-- OpenTelemetry MySQL Schema (MySQL 8.0+)
-- Translated from SqlServer-Schema.sql (the closest relational analog).
--
-- Differences from the SQL Server schema:
--   BIGINT IDENTITY(1,1) PRIMARY KEY  ->  BIGINT AUTO_INCREMENT PRIMARY KEY
--   INT IDENTITY(1,1)                 ->  INT AUTO_INCREMENT
--   NVARCHAR(n)                       ->  VARCHAR(n)
--   NVARCHAR(MAX) (text)             ->  TEXT / LONGTEXT
--   NVARCHAR(MAX) (JSON attributes)  ->  JSON  (native)
--   FLOAT                             ->  DOUBLE
--   BIT                               ->  TINYINT(1)
--   DATETIME2                         ->  DATETIME(6)
--   SYSDATETIME() / GETUTCDATE()      ->  CURRENT_TIMESTAMP(6) / UTC_TIMESTAMP(6)
--   [type] (bracketed reserved word)  ->  type  (TYPE is non-reserved in MySQL)
--   MERGE / ON CONFLICT               ->  INSERT ... ON DUPLICATE KEY UPDATE (runtime, in C#)
--   JSON_VALUE(col, '$.x')            ->  col ->> '$."x"'
--   DATEADD(DAY, n, '1970-01-01')     ->  DATE_ADD('1970-01-01', INTERVAL n DAY)
--   integer division ( / )            ->  DIV   (MySQL '/' is floating-point division)
--   filtered index WHERE enabled = 1  ->  plain index (MySQL has no filtered indexes)
--   inline column REFERENCES          ->  table-level FOREIGN KEY (MySQL ignores inline refs)
--
-- All tables are InnoDB / utf8mb4 so the real ON DELETE CASCADE foreign keys used by
-- the write-path deletes are enforced. TimescaleDB hypertables / compression / retention
-- have no MySQL equivalent and are omitted, as in the SQL Server schema.
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
    CONSTRAINT uk_api_key_hash UNIQUE (key_hash),
    CONSTRAINT fk_api_keys_tenants FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_api_keys_tenant_id ON api_keys (tenant_id);

-- Resource represents the entity producing telemetry.
CREATE TABLE resources (
    id              BIGINT AUTO_INCREMENT PRIMARY KEY,
    tenant_id       BIGINT       NOT NULL DEFAULT 1,
    resource_hash   CHAR(64)     NOT NULL,
    schema_url      VARCHAR(2048),
    created_at      DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    attributes_json JSON,
    CONSTRAINT uk_resource_tenant_hash UNIQUE (tenant_id, resource_hash),
    CONSTRAINT fk_resources_tenants FOREIGN KEY (tenant_id) REFERENCES tenants (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_resources_tenant_id ON resources (tenant_id);
CREATE INDEX idx_created_at          ON resources (created_at);

-- Instrumentation scope (library).
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
CREATE INDEX idx_name_version ON instrumentation_scopes (name, version);

-- =============================================================================
-- TRACES TABLES
-- =============================================================================

CREATE TABLE spans (
    id                       BIGINT AUTO_INCREMENT PRIMARY KEY,
    trace_id                 CHAR(32)     NOT NULL,
    span_id                  CHAR(16)     NOT NULL,
    parent_span_id           CHAR(16),
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
    CONSTRAINT fk_spans_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_spans_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id),
    CONSTRAINT uk_trace_span      UNIQUE (trace_id, span_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_trace_id            ON spans (trace_id);
CREATE INDEX idx_span_id             ON spans (span_id);
CREATE INDEX idx_parent_span         ON spans (parent_span_id);
CREATE INDEX idx_spans_trace_parent  ON spans (trace_id, parent_span_id);
CREATE INDEX idx_start_time          ON spans (start_time_unix_nano DESC);
CREATE INDEX idx_end_time            ON spans (end_time_unix_nano DESC);
CREATE INDEX idx_duration            ON spans (start_time_unix_nano, end_time_unix_nano);
CREATE INDEX idx_spans_name          ON spans (name);
CREATE INDEX idx_kind                ON spans (kind);
CREATE INDEX idx_status              ON spans (status_code);
CREATE INDEX idx_spans_resource_time ON spans (resource_id, start_time_unix_nano DESC);

-- Span events.
CREATE TABLE span_events (
    id                       BIGINT AUTO_INCREMENT PRIMARY KEY,
    span_id                  BIGINT       NOT NULL,
    name                     VARCHAR(255) NOT NULL,
    time_unix_nano           BIGINT       NOT NULL,
    dropped_attributes_count INT          DEFAULT 0,
    attributes_json          JSON,
    CONSTRAINT fk_span_events_spans FOREIGN KEY (span_id) REFERENCES spans (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_span_time ON span_events (span_id, time_unix_nano);

-- Span links.
CREATE TABLE span_links (
    id                       BIGINT AUTO_INCREMENT PRIMARY KEY,
    span_id                  BIGINT   NOT NULL,
    linked_trace_id          CHAR(32) NOT NULL,
    linked_span_id           CHAR(16) NOT NULL,
    trace_state              TEXT,
    flags                    INT      DEFAULT 0,
    dropped_attributes_count INT      DEFAULT 0,
    attributes_json          JSON,
    CONSTRAINT fk_span_links_spans FOREIGN KEY (span_id) REFERENCES spans (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_span_link ON span_links (span_id, linked_trace_id, linked_span_id);

-- =============================================================================
-- METRICS TABLES
-- =============================================================================

CREATE TABLE metrics (
    id          BIGINT AUTO_INCREMENT PRIMARY KEY,
    resource_id BIGINT       NOT NULL,
    scope_id    BIGINT       NOT NULL,
    name        VARCHAR(255) NOT NULL,
    description TEXT,
    unit        VARCHAR(63),
    type        VARCHAR(30)  NOT NULL
        CHECK (type IN ('GAUGE', 'SUM', 'HISTOGRAM', 'EXPONENTIAL_HISTOGRAM', 'SUMMARY')),
    created_at  DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT fk_metrics_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_metrics_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_metrics_name  ON metrics (name);
CREATE INDEX idx_type          ON metrics (type);
CREATE INDEX idx_resource_name ON metrics (resource_id, name);

-- Gauge data points.
CREATE TABLE gauge_data_points (
    id                   BIGINT AUTO_INCREMENT PRIMARY KEY,
    metric_id            BIGINT NOT NULL,
    start_time_unix_nano BIGINT,
    time_unix_nano       BIGINT NOT NULL,
    value_double         DOUBLE,
    value_int            BIGINT,
    flags                INT    DEFAULT 0,
    exemplar_id          BIGINT,
    attributes_json      JSON,
    CONSTRAINT fk_gauge_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_gauge_metric_time ON gauge_data_points (metric_id, time_unix_nano DESC);
CREATE INDEX idx_gauge_time        ON gauge_data_points (time_unix_nano DESC);

-- Sum data points.
CREATE TABLE sum_data_points (
    id                      BIGINT AUTO_INCREMENT PRIMARY KEY,
    metric_id               BIGINT      NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT      NOT NULL,
    value_double            DOUBLE,
    value_int               BIGINT,
    aggregation_temporality VARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    is_monotonic            TINYINT(1)  DEFAULT 0,
    flags                   INT         DEFAULT 0,
    exemplar_id             BIGINT,
    attributes_json         JSON,
    CONSTRAINT fk_sum_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_sum_metric_time ON sum_data_points (metric_id, time_unix_nano DESC);
CREATE INDEX idx_temporality     ON sum_data_points (aggregation_temporality);

-- Histogram data points.
CREATE TABLE histogram_data_points (
    id                      BIGINT      AUTO_INCREMENT PRIMARY KEY,
    metric_id               BIGINT      NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT      NOT NULL,
    count                   BIGINT      NOT NULL,
    sum_value               DOUBLE,
    bucket_counts           JSON,
    explicit_bounds         JSON,
    aggregation_temporality VARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    flags                   INT         DEFAULT 0,
    min_value               DOUBLE,
    max_value               DOUBLE,
    exemplar_id             BIGINT,
    attributes_json         JSON,
    CONSTRAINT fk_histogram_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_histogram_metric_time ON histogram_data_points (metric_id, time_unix_nano DESC);

-- Exponential histogram data points.
CREATE TABLE exponential_histogram_data_points (
    id                      BIGINT      AUTO_INCREMENT PRIMARY KEY,
    metric_id               BIGINT      NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT      NOT NULL,
    count                   BIGINT      NOT NULL,
    sum_value               DOUBLE,
    scale                   INT         NOT NULL,
    zero_count              BIGINT      NOT NULL,
    positive_offset         INT,
    positive_bucket_counts  JSON,
    negative_offset         INT,
    negative_bucket_counts  JSON,
    aggregation_temporality VARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    flags                   INT         DEFAULT 0,
    min_value               DOUBLE,
    max_value               DOUBLE,
    exemplar_id             BIGINT,
    attributes_json         JSON,
    CONSTRAINT fk_exponential_histogram_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_exp_histogram_metric_time ON exponential_histogram_data_points (metric_id, time_unix_nano DESC);

-- Summary data points.
CREATE TABLE summary_data_points (
    id                   BIGINT AUTO_INCREMENT PRIMARY KEY,
    metric_id            BIGINT NOT NULL,
    start_time_unix_nano BIGINT,
    time_unix_nano       BIGINT NOT NULL,
    count                BIGINT NOT NULL,
    sum_value            DOUBLE NOT NULL,
    quantile_values      JSON,
    flags                INT    DEFAULT 0,
    attributes_json      JSON,
    CONSTRAINT fk_summary_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_summary_metric_time ON summary_data_points (metric_id, time_unix_nano DESC);

-- Exemplars (regular table, soft-referenced by data point tables via exemplar_id).
CREATE TABLE exemplars (
    id                  BIGINT AUTO_INCREMENT PRIMARY KEY,
    filtered_attributes JSON,
    time_unix_nano      BIGINT NOT NULL,
    value_double        DOUBLE,
    value_int           BIGINT,
    span_id             CHAR(16),
    trace_id            CHAR(32)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_exemplar_time       ON exemplars (time_unix_nano);
CREATE INDEX idx_exemplar_trace_span ON exemplars (trace_id, span_id);

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

-- Default 0 for time_unix_nano handles OTLP records where TimeUnixNano is absent.
CREATE TABLE log_records (
    id                       BIGINT      AUTO_INCREMENT PRIMARY KEY,
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
    trace_id                 CHAR(32),
    span_id                  CHAR(16),
    created_at               DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    attributes_json          JSON,
    CONSTRAINT fk_log_records_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_log_records_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_log_time          ON log_records (time_unix_nano          DESC);
CREATE INDEX idx_observed_time     ON log_records (observed_time_unix_nano  DESC);
CREATE INDEX idx_severity          ON log_records (severity_number);
CREATE INDEX idx_log_severity_time ON log_records (severity_number, time_unix_nano DESC);
CREATE INDEX idx_log_trace_span    ON log_records (trace_id, span_id);
CREATE INDEX idx_log_resource_time ON log_records (resource_id, time_unix_nano DESC);

-- =============================================================================
-- UTILITY TABLES
-- =============================================================================

CREATE TABLE schema_version (
    version    VARCHAR(20) PRIMARY KEY,
    applied_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
-- NOTE: the schema_version row is seeded at the very END of this script (after all
-- tables and views), so a partial/failed apply never records a version that the
-- apply-schema.sh version gate would wrongly treat as "already applied".

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
-- VIEWS
-- =============================================================================

DROP VIEW IF EXISTS log_severity_stats;
DROP VIEW IF EXISTS service_map_detailed;
DROP VIEW IF EXISTS service_map;
DROP VIEW IF EXISTS trace_summary;

-- Trace summary: aggregated span counts and timing per trace.
CREATE VIEW trace_summary AS
SELECT
    s.trace_id                                                AS trace_id_hex,
    s.trace_id,
    COUNT(*)                                                  AS span_count,
    MIN(s.start_time_unix_nano)                               AS trace_start_time,
    MAX(s.end_time_unix_nano)                                 AS trace_end_time,
    MAX(s.end_time_unix_nano) - MIN(s.start_time_unix_nano)   AS trace_duration_ns,
    r.id                                                      AS resource_id
FROM spans s
JOIN resources r ON s.resource_id = r.id
GROUP BY s.trace_id, r.id;

-- Service map: service-to-service call relationships extracted from span parent-child pairs.
-- The ->> operator (JSON_UNQUOTE(JSON_EXTRACT(...))) replaces SQL Server's JSON_VALUE.
-- The attribute key "service.name" contains a dot, so the JSON path quotes it: $."service.name".
CREATE VIEW service_map AS
SELECT
    parent_res.attributes_json ->> '$."service.name"' AS parent_service,
    child_res.attributes_json  ->> '$."service.name"' AS child_service,
    child.kind                                         AS span_kind,
    COUNT(*)                                           AS call_count
FROM spans child
INNER JOIN spans parent
    ON child.parent_span_id = parent.span_id
   AND child.trace_id       = parent.trace_id
INNER JOIN resources parent_res ON parent.resource_id = parent_res.id
INNER JOIN resources child_res  ON child.resource_id  = child_res.id
WHERE
    parent_res.attributes_json ->> '$."service.name"' IS NOT NULL
    AND child_res.attributes_json ->> '$."service.name"' IS NOT NULL
    AND parent_res.attributes_json ->> '$."service.name"' <>
        child_res.attributes_json  ->> '$."service.name"'
GROUP BY
    parent_res.attributes_json ->> '$."service.name"',
    child_res.attributes_json  ->> '$."service.name"',
    child.kind;

-- Service map with performance metrics.
CREATE VIEW service_map_detailed AS
SELECT
    parent_res.attributes_json ->> '$."service.name"'                              AS parent_service,
    child_res.attributes_json  ->> '$."service.name"'                              AS child_service,
    child.kind                                                                     AS span_kind,
    COUNT(*)                                                                       AS call_count,
    AVG(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000           AS avg_duration_ms,
    MIN(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000           AS min_duration_ms,
    MAX(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000           AS max_duration_ms,
    SUM(CASE WHEN child.status_code = 'ERROR' THEN 1 ELSE 0 END)                   AS error_count,
    SUM(CASE WHEN child.status_code = 'ERROR' THEN 1 ELSE 0 END) / COUNT(*) * 100  AS error_rate
FROM spans child
INNER JOIN spans parent
    ON child.parent_span_id = parent.span_id
   AND child.trace_id       = parent.trace_id
INNER JOIN resources parent_res ON parent.resource_id = parent_res.id
INNER JOIN resources child_res  ON child.resource_id  = child_res.id
WHERE
    parent_res.attributes_json ->> '$."service.name"' IS NOT NULL
    AND child_res.attributes_json ->> '$."service.name"' IS NOT NULL
    AND parent_res.attributes_json ->> '$."service.name"' <>
        child_res.attributes_json  ->> '$."service.name"'
GROUP BY
    parent_res.attributes_json ->> '$."service.name"',
    child_res.attributes_json  ->> '$."service.name"',
    child.kind;

-- Log severity distribution by day.
-- SQL Server used a regular view; MySQL does the same (computed on demand).
-- The day bucket integer-divides nanoseconds down to whole days since epoch (DIV, since
-- MySQL '/' is floating-point), then converts back to a DATE via DATE_ADD.
CREATE VIEW log_severity_stats AS
WITH bucketed AS (
    SELECT
        severity_text,
        severity_number,
        (time_unix_nano DIV 1000000000 DIV 86400) AS day_bucket
    FROM log_records
    WHERE time_unix_nano > 0
)
SELECT
    severity_text,
    severity_number,
    COUNT(*)                                       AS count,
    DATE_ADD('1970-01-01', INTERVAL day_bucket DAY) AS log_date
FROM bucketed
GROUP BY severity_text, severity_number, day_bucket;

-- =============================================================================
-- SCHEMA VERSION (recorded LAST)
-- =============================================================================
-- Only inserted when every statement above succeeded, so a partial apply cannot
-- leave a false version marker for the apply-schema.sh gate.
INSERT INTO schema_version (version, applied_at)
VALUES ('2.6.0', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);

-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) List all base tables (expect 18)
--    SELECT table_name FROM information_schema.tables
--    WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' ORDER BY table_name;
--
-- 2) List all views (expect 4)
--    SELECT table_name FROM information_schema.views
--    WHERE table_schema = DATABASE() ORDER BY table_name;
--
-- 3) Verify schema version
--    SELECT * FROM schema_version;
