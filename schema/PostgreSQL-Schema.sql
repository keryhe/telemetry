-- OpenTelemetry PostgreSQL + TimescaleDB Schema
-- Supports OTLP logs, metrics, and traces as defined in opentelemetry-proto
-- Requires TimescaleDB extension (https://docs.timescale.com/install/latest/)
--
-- Column names use snake_case (double-quoted) while C# models remain PascalCase.
-- Table names use snake_case as configured via ToTable() in OpenTelemetryDbContext.
--
-- Usage:
--   psql -U postgres -c "CREATE DATABASE telemetry;"
--   psql -U postgres -d telemetry -f PostgreSQL-Schema.sql
--
-- Optional clean reset in an existing local DB before re-running this script:
--   psql -U postgres -d telemetry -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"

-- =============================================================================
-- EXTENSIONS
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS timescaledb;

-- =============================================================================
-- COMMON TABLES (shared across signals)
-- =============================================================================

CREATE TABLE tenants (
    "id"        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "name"      VARCHAR(255) NOT NULL,
    "created_at" TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT uk_tenant_name UNIQUE ("name")
);

INSERT INTO tenants ("id", "name")
OVERRIDING SYSTEM VALUE
VALUES (1, 'default')
ON CONFLICT ("name") DO NOTHING;

CREATE TABLE api_keys (
    "id"         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "tenant_id"   BIGINT       NOT NULL REFERENCES tenants("id") ON DELETE CASCADE,
    "key_hash"    CHAR(64)     NOT NULL,
    "name"       VARCHAR(255) NOT NULL,
    "is_active"   BOOLEAN      NOT NULL DEFAULT TRUE,
    "created_at"  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "last_used_at" TIMESTAMPTZ,
    CONSTRAINT uk_api_key_hash UNIQUE ("key_hash")
);
CREATE INDEX idx_api_keys_tenant_id ON api_keys ("tenant_id");

-- Resource represents the entity producing telemetry
CREATE TABLE resources (
    "id"             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "tenant_id"       BIGINT       NOT NULL DEFAULT 1 REFERENCES tenants("id"),
    "resource_hash"   CHAR(64)     NOT NULL,
    "schema_url"      VARCHAR(2048),
    "created_at"      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json" JSONB,
    CONSTRAINT uk_resource_tenant_hash UNIQUE ("tenant_id", "resource_hash")
);
CREATE INDEX idx_resources_tenant_id ON resources ("tenant_id");
CREATE INDEX idx_created_at ON resources ("created_at");
CREATE INDEX idx_resources_service_name ON resources (("attributes_json" ->> 'service.name'));

-- Instrumentation scope (library)
CREATE TABLE instrumentation_scopes (
    "id"             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "name"           VARCHAR(255) NOT NULL,
    "version"        VARCHAR(255),
    "schema_url"      VARCHAR(2048),
    "scope_hash"      CHAR(64)     NOT NULL,
    "created_at"      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json" JSONB,
    CONSTRAINT uk_scope_hash UNIQUE ("scope_hash")
);
CREATE INDEX idx_name_version ON instrumentation_scopes ("name", "version");

-- =============================================================================
-- TRACES TABLES
-- =============================================================================

-- Trace spans: regular PostgreSQL table (not a hypertable).
-- span_events and span_links hold FK references to spans("id"), which requires
-- a simple primary key. Use idx_start_time for time-range queries instead.
CREATE TABLE spans (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "trace_id"                CHAR(32)     NOT NULL,
    "span_id"                 CHAR(16)     NOT NULL,
    "parent_span_id"           CHAR(16),
    "resource_id"             BIGINT       NOT NULL,
    "scope_id"                BIGINT       NOT NULL,
    "name"                   VARCHAR(255) NOT NULL,
    "kind"                   VARCHAR(20)  NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK ("kind" IN ('UNSPECIFIED', 'INTERNAL', 'SERVER', 'CLIENT', 'PRODUCER', 'CONSUMER')),
    "start_time_unix_nano"      BIGINT       NOT NULL,
    "end_time_unix_nano"        BIGINT       NOT NULL,
    "dropped_attributes_count" INTEGER      DEFAULT 0,
    "dropped_events_count"     INTEGER      DEFAULT 0,
    "dropped_links_count"      INTEGER      DEFAULT 0,
    "trace_state"             TEXT,
    "status_code"             VARCHAR(20)  NOT NULL DEFAULT 'UNSET'
        CHECK ("status_code" IN ('UNSET', 'OK', 'ERROR')),
    "status_message"          TEXT,
    "created_at"              TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json"         JSONB,
    CONSTRAINT fk_spans_resources FOREIGN KEY ("resource_id") REFERENCES resources ("id"),
    CONSTRAINT fk_spans_scopes    FOREIGN KEY ("scope_id")    REFERENCES instrumentation_scopes ("id"),
    CONSTRAINT uk_trace_span      UNIQUE ("trace_id", "span_id")
);
CREATE INDEX idx_trace_id           ON spans ("trace_id");
CREATE INDEX idx_span_id            ON spans ("span_id");
CREATE INDEX idx_parent_span        ON spans ("parent_span_id");
CREATE INDEX idx_spans_trace_parent ON spans ("trace_id", "parent_span_id");
CREATE INDEX idx_start_time         ON spans ("start_time_unix_nano" DESC);
CREATE INDEX idx_end_time           ON spans ("end_time_unix_nano"   DESC);
CREATE INDEX idx_duration           ON spans ("start_time_unix_nano", "end_time_unix_nano");
CREATE INDEX idx_spans_name         ON spans ("name");
CREATE INDEX idx_kind               ON spans ("kind");
CREATE INDEX idx_status             ON spans ("status_code");
CREATE INDEX idx_spans_resource_time ON spans ("resource_id", "start_time_unix_nano" DESC);
CREATE INDEX idx_spans_attributes_gin ON spans USING GIN ("attributes_json");

-- Span events
CREATE TABLE span_events (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "span_id"                 BIGINT       NOT NULL,
    "name"                   VARCHAR(255) NOT NULL,
    "time_unix_nano"           BIGINT       NOT NULL,
    "dropped_attributes_count" INTEGER      DEFAULT 0,
    "attributes_json"         JSONB,
    CONSTRAINT fk_span_events_spans FOREIGN KEY ("span_id") REFERENCES spans ("id") ON DELETE CASCADE
);
CREATE INDEX idx_span_time ON span_events ("span_id", "time_unix_nano");

-- Span links
CREATE TABLE span_links (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "span_id"                 BIGINT    NOT NULL,
    "linked_trace_id"          CHAR(32)  NOT NULL,
    "linked_span_id"           CHAR(16)  NOT NULL,
    "trace_state"             TEXT,
    "dropped_attributes_count" INTEGER   DEFAULT 0,
    "attributes_json"         JSONB,
    CONSTRAINT fk_span_links_spans FOREIGN KEY ("span_id") REFERENCES spans ("id") ON DELETE CASCADE
);
CREATE INDEX idx_span_link ON span_links ("span_id", "linked_trace_id", "linked_span_id");

-- =============================================================================
-- METRICS TABLES
-- =============================================================================

-- Base metrics table (regular table  referenced by FK from data point tables)
CREATE TABLE metrics (
    "id"          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "resource_id"  BIGINT       NOT NULL,
    "scope_id"     BIGINT       NOT NULL,
    "name"        VARCHAR(255) NOT NULL,
    "description" TEXT,
    "unit"        VARCHAR(63),
    "type"        VARCHAR(30)  NOT NULL
        CHECK ("type" IN ('GAUGE', 'SUM', 'HISTOGRAM', 'EXPONENTIAL_HISTOGRAM', 'SUMMARY')),
    "created_at"   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT fk_metrics_resources FOREIGN KEY ("resource_id") REFERENCES resources ("id"),
    CONSTRAINT fk_metrics_scopes    FOREIGN KEY ("scope_id")    REFERENCES instrumentation_scopes ("id")
);
CREATE INDEX idx_metrics_name  ON metrics ("name");
CREATE INDEX idx_type          ON metrics ("type");
CREATE INDEX idx_resource_name ON metrics ("resource_id", "name");

-- TimescaleDB chunk sizing baseline (nanoseconds):
--   1 hour  =  3600000000000
--   6 hours = 21600000000000
--   12 hours= 43200000000000
--   1 day   = 86400000000000
--
-- Initial Phase 1 target:
-- - Keep chunks in the ~256 MB to 1 GB range under normal ingest.
-- - Use shorter chunks for higher-volume signals (logs), longer chunks for
--   lower-volume metric point tables until real ingest data is available.

-- Gauge data points (TimescaleDB hypertable on TimeUnixNano)
-- No PRIMARY KEY: TimescaleDB requires unique constraints to include the partition
-- column; since nothing FK-references this table's Id, a DB-level PK is not needed.
CREATE TABLE gauge_data_points (
    "id"                BIGINT GENERATED ALWAYS AS IDENTITY,
    "metric_id"          BIGINT           NOT NULL,
    "start_time_unix_nano" BIGINT,
    "time_unix_nano"      BIGINT           NOT NULL,
    "value_double"       DOUBLE PRECISION,
    "value_int"          BIGINT,
    "flags"             INTEGER          DEFAULT 0,
    "exemplar_id"        BIGINT,
    "attributes_json"    JSONB,
    CONSTRAINT fk_gauge_data_points_metrics FOREIGN KEY ("metric_id") REFERENCES metrics ("id") ON DELETE CASCADE
);
SELECT create_hypertable('gauge_data_points', 'time_unix_nano',
    chunk_time_interval => 43200000000000,
    if_not_exists => TRUE
);
CREATE INDEX idx_gauge_metric_time ON gauge_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_gauge_time        ON gauge_data_points ("time_unix_nano" DESC);

-- Sum data points (TimescaleDB hypertable on TimeUnixNano)
CREATE TABLE sum_data_points (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "metric_id"               BIGINT           NOT NULL,
    "start_time_unix_nano"      BIGINT,
    "time_unix_nano"           BIGINT           NOT NULL,
    "value_double"            DOUBLE PRECISION,
    "value_int"               BIGINT,
    "aggregation_temporality" TEXT         NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK ("aggregation_temporality" IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    "is_monotonic"            BOOLEAN          DEFAULT FALSE,
    "flags"                  INTEGER          DEFAULT 0,
    "exemplar_id"             BIGINT,
    "attributes_json"         JSONB,
    CONSTRAINT fk_sum_data_points_metrics FOREIGN KEY ("metric_id") REFERENCES metrics ("id") ON DELETE CASCADE
);
SELECT create_hypertable('sum_data_points', 'time_unix_nano',
    chunk_time_interval => 43200000000000,
    if_not_exists => TRUE
);
CREATE INDEX idx_sum_metric_time ON sum_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_temporality     ON sum_data_points ("aggregation_temporality");

-- Histogram data points (TimescaleDB hypertable on TimeUnixNano)
CREATE TABLE histogram_data_points (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "metric_id"               BIGINT           NOT NULL,
    "start_time_unix_nano"      BIGINT,
    "time_unix_nano"           BIGINT           NOT NULL,
    "count"                  BIGINT           NOT NULL,
    "sum_value"               DOUBLE PRECISION,
    "bucket_counts"           JSONB,
    "explicit_bounds"         JSONB,
    "aggregation_temporality" TEXT         NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK ("aggregation_temporality" IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    "flags"                  INTEGER          DEFAULT 0,
    "min_value"              DOUBLE PRECISION,
    "max_value"              DOUBLE PRECISION,
    "exemplar_id"             BIGINT,
    "attributes_json"         JSONB,
    CONSTRAINT fk_histogram_data_points_metrics FOREIGN KEY ("metric_id") REFERENCES metrics ("id") ON DELETE CASCADE
);
SELECT create_hypertable('histogram_data_points', 'time_unix_nano',
    chunk_time_interval => 86400000000000,
    if_not_exists => TRUE
);
CREATE INDEX idx_histogram_metric_time ON histogram_data_points ("metric_id", "time_unix_nano" DESC);

-- Exponential histogram data points (TimescaleDB hypertable on TimeUnixNano)
CREATE TABLE exponential_histogram_data_points (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "metric_id"               BIGINT           NOT NULL,
    "start_time_unix_nano"      BIGINT,
    "time_unix_nano"           BIGINT           NOT NULL,
    "count"                  BIGINT           NOT NULL,
    "sum_value"               DOUBLE PRECISION,
    "scale"                  INTEGER          NOT NULL,
    "zero_count"              BIGINT           NOT NULL,
    "positive_offset"         INTEGER,
    "positive_bucket_counts"   JSONB,
    "negative_offset"         INTEGER,
    "negative_bucket_counts"   JSONB,
    "aggregation_temporality" TEXT         NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK ("aggregation_temporality" IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    "flags"                  INTEGER          DEFAULT 0,
    "min_value"              DOUBLE PRECISION,
    "max_value"              DOUBLE PRECISION,
    "exemplar_id"             BIGINT,
    "attributes_json"         JSONB,
    CONSTRAINT fk_exponential_histogram_data_points_metrics FOREIGN KEY ("metric_id") REFERENCES metrics ("id") ON DELETE CASCADE
);
SELECT create_hypertable('exponential_histogram_data_points', 'time_unix_nano',
    chunk_time_interval => 86400000000000,
    if_not_exists => TRUE
);
CREATE INDEX idx_exp_histogram_metric_time ON exponential_histogram_data_points ("metric_id", "time_unix_nano" DESC);

-- Summary data points (TimescaleDB hypertable on TimeUnixNano)
CREATE TABLE summary_data_points (
    "id"                BIGINT GENERATED ALWAYS AS IDENTITY,
    "metric_id"          BIGINT           NOT NULL,
    "start_time_unix_nano" BIGINT,
    "time_unix_nano"      BIGINT           NOT NULL,
    "count"             BIGINT           NOT NULL,
    "sum_value"          DOUBLE PRECISION NOT NULL,
    "quantile_values"    JSONB,
    "flags"             INTEGER          DEFAULT 0,
    "attributes_json"    JSONB,
    CONSTRAINT fk_summary_data_points_metrics FOREIGN KEY ("metric_id") REFERENCES metrics ("id") ON DELETE CASCADE
);
SELECT create_hypertable('summary_data_points', 'time_unix_nano',
    chunk_time_interval => 86400000000000,
    if_not_exists => TRUE
);
CREATE INDEX idx_summary_metric_time ON summary_data_points ("metric_id", "time_unix_nano" DESC);

-- Exemplars (for metrics  regular table, referenced by FK from data point tables)
CREATE TABLE exemplars (
    "id"                 BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "filtered_attributes" JSONB,
    "time_unix_nano"       BIGINT NOT NULL,
    "value_double"        DOUBLE PRECISION,
    "value_int"           BIGINT,
    "span_id"             CHAR(16),
    "trace_id"            CHAR(32)
);
CREATE INDEX idx_exemplar_time       ON exemplars ("time_unix_nano");
CREATE INDEX idx_exemplar_trace_span ON exemplars ("trace_id", "span_id");

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

-- Log records (TimescaleDB hypertable on TimeUnixNano)
-- TimeUnixNano is NOT NULL (required for hypertable partition column).
-- Default 0 handles any edge-case OTLP records where TimeUnixNano is absent.
CREATE TABLE log_records (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "resource_id"             BIGINT       NOT NULL,
    "scope_id"                BIGINT       NOT NULL,
    "time_unix_nano"           BIGINT       NOT NULL DEFAULT 0,
    "observed_time_unix_nano"   BIGINT,
    "severity_number"         INTEGER,
    "severity_text"           TEXT,
    "body_type"               TEXT         DEFAULT 'STRING'
        CHECK ("body_type" IN ('STRING', 'BOOL', 'INT', 'DOUBLE', 'BYTES', 'ARRAY', 'KVLIST')),
    "body_value"              TEXT,
    "dropped_attributes_count" INTEGER      DEFAULT 0,
    "flags"                  INTEGER      DEFAULT 0,
    "trace_id"                CHAR(32),
    "span_id"                 CHAR(16),
    "created_at"              TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json"         JSONB,
    CONSTRAINT fk_log_records_resources FOREIGN KEY ("resource_id") REFERENCES resources ("id"),
    CONSTRAINT fk_log_records_scopes    FOREIGN KEY ("scope_id")    REFERENCES instrumentation_scopes ("id")
);
SELECT create_hypertable('log_records', 'time_unix_nano',
    chunk_time_interval => 21600000000000,
    if_not_exists => TRUE
);
CREATE INDEX idx_log_time          ON log_records ("time_unix_nano"         DESC);
CREATE INDEX idx_observed_time     ON log_records ("observed_time_unix_nano" DESC);
CREATE INDEX idx_severity          ON log_records ("severity_number");
CREATE INDEX idx_log_severity_time ON log_records ("severity_number", "time_unix_nano" DESC);
CREATE INDEX idx_log_trace_span    ON log_records ("trace_id", "span_id");
CREATE INDEX idx_log_resource_time ON log_records ("resource_id", "time_unix_nano" DESC);
CREATE INDEX idx_log_attributes_gin ON log_records USING GIN ("attributes_json");

-- =============================================================================
-- TIMESCALEDB LIFECYCLE POLICIES (PHASE 2)
-- =============================================================================

-- Integer-time policy constants (nanoseconds)
--   7 days   =   604800000000000
--   90 days  =  7776000000000000
--   180 days = 15552000000000000

-- Integer time source for BIGINT nanosecond hypertables.
CREATE OR REPLACE FUNCTION telemetry_now_ns()
RETURNS BIGINT
LANGUAGE SQL
STABLE
AS $$
    SELECT (EXTRACT(EPOCH FROM NOW()) * 1000000000)::BIGINT;
$$;

-- Register integer-now function for each hypertable.
SELECT set_integer_now_func('gauge_data_points', 'telemetry_now_ns');
SELECT set_integer_now_func('sum_data_points', 'telemetry_now_ns');
SELECT set_integer_now_func('histogram_data_points', 'telemetry_now_ns');
SELECT set_integer_now_func('exponential_histogram_data_points', 'telemetry_now_ns');
SELECT set_integer_now_func('summary_data_points', 'telemetry_now_ns');
SELECT set_integer_now_func('log_records', 'telemetry_now_ns');

-- Enable compression with segment/order strategy tuned for common query paths.
ALTER TABLE gauge_data_points SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"metric_id"',
    timescaledb.compress_orderby = '"time_unix_nano" DESC'
);
ALTER TABLE sum_data_points SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"metric_id"',
    timescaledb.compress_orderby = '"time_unix_nano" DESC'
);
ALTER TABLE histogram_data_points SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"metric_id"',
    timescaledb.compress_orderby = '"time_unix_nano" DESC'
);
ALTER TABLE exponential_histogram_data_points SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"metric_id"',
    timescaledb.compress_orderby = '"time_unix_nano" DESC'
);
ALTER TABLE summary_data_points SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"metric_id"',
    timescaledb.compress_orderby = '"time_unix_nano" DESC'
);
ALTER TABLE log_records SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"resource_id", "scope_id"',
    timescaledb.compress_orderby = '"time_unix_nano" DESC'
);

-- Compression policies (cold data).
SELECT add_compression_policy('gauge_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('sum_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('histogram_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('exponential_histogram_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('summary_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('log_records', BIGINT '604800000000000', if_not_exists => TRUE);

-- Retention policies (drop old data).
SELECT add_retention_policy('log_records', BIGINT '7776000000000000', if_not_exists => TRUE);
SELECT add_retention_policy('gauge_data_points', BIGINT '15552000000000000', if_not_exists => TRUE);
SELECT add_retention_policy('sum_data_points', BIGINT '15552000000000000', if_not_exists => TRUE);
SELECT add_retention_policy('histogram_data_points', BIGINT '15552000000000000', if_not_exists => TRUE);
SELECT add_retention_policy('exponential_histogram_data_points', BIGINT '15552000000000000', if_not_exists => TRUE);
SELECT add_retention_policy('summary_data_points', BIGINT '15552000000000000', if_not_exists => TRUE);

-- =============================================================================
-- UTILITY TABLES
-- =============================================================================

CREATE TABLE schema_version (
    "version"   VARCHAR(20) PRIMARY KEY,
    "applied_at" TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO schema_version ("version") VALUES ('2.5.0')
ON CONFLICT ("version") DO UPDATE
SET "applied_at" = NOW();

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
-- VIEWS
-- =============================================================================

DROP VIEW IF EXISTS log_severity_stats;
DROP MATERIALIZED VIEW IF EXISTS log_severity_stats_daily;
DROP VIEW IF EXISTS service_map_detailed;
DROP VIEW IF EXISTS service_map;
DROP VIEW IF EXISTS trace_summary;

-- Trace summary: aggregated span counts and timing per trace
CREATE VIEW trace_summary AS
SELECT
    s."trace_id"::TEXT                                       AS "trace_id_hex",
    s."trace_id",
    COUNT(*)                                                AS "span_count",
    MIN(s."start_time_unix_nano")                              AS "trace_start_time",
    MAX(s."end_time_unix_nano")                                AS "trace_end_time",
    MAX(s."end_time_unix_nano") - MIN(s."start_time_unix_nano")   AS "trace_duration_ns",
    r."id"                                                  AS "resource_id"
FROM spans s
JOIN resources r ON s."resource_id" = r."id"
GROUP BY s."trace_id", r."id";

-- Service map: service-to-service call relationships extracted from span parent-child pairs
CREATE VIEW service_map AS
SELECT
    parent_res."attributes_json" ->> 'service.name'   AS "parent_service",
    child_res."attributes_json"  ->> 'service.name'   AS "child_service",
    child."kind"                                     AS "span_kind",
    COUNT(*)                                         AS "call_count"
FROM spans child
INNER JOIN spans parent
    ON child."parent_span_id" = parent."span_id"
    AND child."trace_id"     = parent."trace_id"
INNER JOIN resources parent_res ON parent."resource_id" = parent_res."id"
INNER JOIN resources child_res  ON child."resource_id"  = child_res."id"
WHERE
    parent_res."attributes_json" ->> 'service.name' IS NOT NULL
    AND child_res."attributes_json"  ->> 'service.name' IS NOT NULL
    AND parent_res."attributes_json" ->> 'service.name' <>
        child_res."attributes_json"  ->> 'service.name'
GROUP BY
    parent_res."attributes_json" ->> 'service.name',
    child_res."attributes_json"  ->> 'service.name',
    child."kind";

-- Service map with performance metrics
CREATE VIEW service_map_detailed AS
SELECT
    parent_res."attributes_json" ->> 'service.name'   AS "parent_service",
    child_res."attributes_json"  ->> 'service.name'   AS "child_service",
    child."kind"                                     AS "span_kind",
    COUNT(*)                                         AS "call_count",
    AVG(CAST(child."end_time_unix_nano" - child."start_time_unix_nano" AS DOUBLE PRECISION)) / 1000000 AS "avg_duration_ms",
    MIN(child."end_time_unix_nano" - child."start_time_unix_nano") / 1000000                            AS "min_duration_ms",
    MAX(child."end_time_unix_nano" - child."start_time_unix_nano") / 1000000                            AS "max_duration_ms",
    SUM(CASE WHEN child."status_code" = 'ERROR' THEN 1 ELSE 0 END)                                AS "error_count",
    (CAST(SUM(CASE WHEN child."status_code" = 'ERROR' THEN 1 ELSE 0 END) AS DOUBLE PRECISION)
        / COUNT(*)) * 100                                                                          AS "error_rate"
FROM spans child
INNER JOIN spans parent
    ON child."parent_span_id" = parent."span_id"
    AND child."trace_id"     = parent."trace_id"
INNER JOIN resources parent_res ON parent."resource_id" = parent_res."id"
INNER JOIN resources child_res  ON child."resource_id"  = child_res."id"
WHERE
    parent_res."attributes_json" ->> 'service.name' IS NOT NULL
    AND child_res."attributes_json"  ->> 'service.name' IS NOT NULL
    AND parent_res."attributes_json" ->> 'service.name' <>
        child_res."attributes_json"  ->> 'service.name'
GROUP BY
    parent_res."attributes_json" ->> 'service.name',
    child_res."attributes_json"  ->> 'service.name',
    child."kind";

-- Log severity distribution by day (continuous aggregate on log_records hypertable)
CREATE MATERIALIZED VIEW log_severity_stats_daily
WITH (timescaledb.continuous) AS
SELECT
    to_timestamp(time_bucket(86400000000000::BIGINT, "time_unix_nano") / 1000000000.0) AS "bucket_day",
    "severity_text",
    "severity_number",
    COUNT(*) AS "count"
FROM log_records
WHERE "time_unix_nano" > 0
GROUP BY
    time_bucket(86400000000000::BIGINT, "time_unix_nano"),
    "severity_text",
    "severity_number"
WITH NO DATA;

CREATE INDEX idx_log_severity_stats_daily_bucket
    ON log_severity_stats_daily ("bucket_day" DESC, "severity_number");

SELECT add_continuous_aggregate_policy(
    'log_severity_stats_daily',
    start_offset => 3024000000000000::BIGINT,
    end_offset => 300000000000::BIGINT,
    schedule_interval => INTERVAL '5 minutes',
    if_not_exists => TRUE
);

ALTER MATERIALIZED VIEW log_severity_stats_daily SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"severity_number", "severity_text"',
    timescaledb.compress_orderby = '"bucket_day" DESC'
);

SELECT add_compression_policy('log_severity_stats_daily', 1209600000000000::BIGINT, if_not_exists => TRUE);
SELECT add_retention_policy('log_severity_stats_daily', 34560000000000000::BIGINT, if_not_exists => TRUE);

-- Backward-compatible view name retained for existing query surfaces.
CREATE VIEW log_severity_stats AS
SELECT
    "severity_text",
    "severity_number",
    "count",
    CAST("bucket_day" AS DATE) AS "log_date"
FROM log_severity_stats_daily;

-- =============================================================================
-- NOTES
-- =============================================================================
--
-- Key conversion notes from SQL Server to PostgreSQL + TimescaleDB:
--
-- 1.  BIGINT IDENTITY(1,1)   BIGINT GENERATED ALWAYS AS IDENTITY
-- 2.  NVARCHAR(n)            VARCHAR(n)  (PostgreSQL is Unicode by default)
-- 3.  NVARCHAR(MAX)          TEXT
-- 4.  FLOAT                  DOUBLE PRECISION
-- 5.  BIT                    BOOLEAN
-- 6.  DATETIME2              TIMESTAMPTZ
-- 7.  SYSDATETIME()          NOW()
-- 8.  ISJSON(col) = 1        Removed; JSONB type enforces valid JSON natively
-- 9.  JSON columns           JSONB for efficient operator-based querying
-- 10. JSON_VALUE(col, '$."key"')  col ->> 'key'
-- 11. DATEADD(SECOND, ns/1e9, '1970-01-01')  to_timestamp(ns / 1000000000.0)
-- 12. CONVERT(NVARCHAR, col)      col::TEXT
-- 13. CAST(x AS FLOAT)     CAST(x AS DOUBLE PRECISION)
-- 14. GO batch separator    Removed (not used in PostgreSQL)
-- 15. uk_trace_span         (TraceId, SpanId)  spans is a regular table;
--                            hypertable requirement was removed for spans
-- 16. Index names are globally unique (prefixed by table abbreviation where needed)
--
-- TimescaleDB hypertables (partitioned by TimeUnixNano):
--   log_records                        = 6-hour chunks
--   gauge_data_points, sum_data_points = 12-hour chunks
--   histogram_data_points,
--   exponential_histogram_data_points,
--   summary_data_points                = 1-day chunks
--
-- Why spans is NOT a hypertable:
--   span_events and span_links hold FK references to spans("id"). TimescaleDB
--   requires all unique/PK constraints to include the partition column, which
--   would break these normalized FK relationships. spans uses idx_start_time
--   for time-range query performance instead.
--
-- Hypertable leaf tables have no PRIMARY KEY constraint (only GENERATED ALWAYS
-- AS IDENTITY). TimescaleDB disallows unique constraints that exclude the
-- partition column. Since nothing FK-references these tables by Id, a DB-level
-- PK is not needed. EF Core uses Id as the logical primary key and reads the
-- generated value via RETURNING on INSERT.
--
-- Scaling considerations:
-- 1. Adjust chunk_time_interval based on ingestion volume
--    (e.g., 1 hour = 3600000000000 ns for very high-volume environments)
-- 2. Compression defaults are enabled at 7 days for all hypertables.
-- 3. Retention defaults are enabled:
--    - logs: 90 days
--    - metric point hypertables: 180 days
-- 4. Integer-time now function (telemetry_now_ns) is registered for each
--    hypertable so policy jobs operate correctly with BIGINT nanosecond time.
-- 5. Phase 3 query-path indexes include:
--    - resources(service.name expression)
--    - spans(trace,parent-span) and spans/log_records JSONB GIN
--    - log_records(severity,time)
-- 6. Phase 4 adds a continuous aggregate for daily log severity trends with
--    refresh/compression/retention policies and a compatibility view.
-- 7. Phase 5 hardening:
--    - create_hypertable uses if_not_exists => TRUE
--    - views/continuous aggregate are dropped and recreated safely
--    - schema_version write is idempotent via ON CONFLICT
-- 8. Consider continuous aggregates for pre-computed service map metrics

-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) Hypertables and chunk interval overview
--    SELECT hypertable_name, chunk_interval
--    FROM timescaledb_information.dimensions
--    WHERE hypertable_name IN (
--      'log_records', 'gauge_data_points', 'sum_data_points',
--      'histogram_data_points', 'exponential_histogram_data_points',
--      'summary_data_points'
--    )
--    ORDER BY hypertable_name;
--
-- 2) Compression and policy jobs
--    SELECT hypertable_name, compression_enabled
--    FROM timescaledb_information.hypertables
--    WHERE hypertable_name IN (
--      'log_records', 'gauge_data_points', 'sum_data_points',
--      'histogram_data_points', 'exponential_histogram_data_points',
--      'summary_data_points'
--    )
--    ORDER BY hypertable_name;
--
--    SELECT proc_name, hypertable_name, schedule_interval
--    FROM timescaledb_information.jobs
--    ORDER BY proc_name, hypertable_name;
--
-- 3) Continuous aggregate status
--    SELECT view_name, materialized_only, compression_enabled
--    FROM timescaledb_information.continuous_aggregates
--    WHERE view_name = 'log_severity_stats_daily';

