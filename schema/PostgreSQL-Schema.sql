-- OpenTelemetry PostgreSQL Schema (plain PostgreSQL, no TimescaleDB)
-- Supports OTLP logs, metrics, and traces as defined in opentelemetry-proto
-- Targets a vanilla PostgreSQL instance WITHOUT the timescaledb extension.
--
-- Column names use snake_case (double-quoted) while C# models remain PascalCase.
--
-- This script produces the same logical table/column set as Timescale-Schema.sql.
-- The difference is purely physical storage: the metric data-point tables and
-- log_records are plain heap tables here (not hypertables). Time-series access is
-- served by BRIN indexes on "time_unix_nano" plus the natural btree lookup indexes
-- that hypertable partitioning would otherwise provide. The TimescaleDB continuous
-- aggregate is replaced by a plain on-demand view.
--
-- Usage:
--   psql -U postgres -c "CREATE DATABASE telemetry;"
--   psql -U postgres -d telemetry -f PostgreSQL-Schema.sql
--
-- For a TimescaleDB-enabled instance (hypertables, compression, retention,
-- continuous aggregate) use Timescale-Schema.sql instead.
--
-- Optional clean reset in an existing local DB before re-running this script:
--   psql -U postgres -d telemetry -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"

-- =============================================================================
-- COMMON TABLES (shared across signals)
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

-- Trace spans. span_events and span_links hold FK references to spans("id").
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

-- Base metrics table (referenced by FK from data point tables)
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

-- Gauge data points (plain table; BRIN on time + btree natural lookup)
CREATE TABLE gauge_data_points (
    "id"                BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
CREATE INDEX idx_gauge_metric_time ON gauge_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_gauge_time_brin   ON gauge_data_points USING BRIN ("time_unix_nano");

-- Sum data points (plain table; BRIN on time + btree natural lookup)
CREATE TABLE sum_data_points (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
CREATE INDEX idx_sum_metric_time ON sum_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_sum_time_brin   ON sum_data_points USING BRIN ("time_unix_nano");
CREATE INDEX idx_temporality     ON sum_data_points ("aggregation_temporality");

-- Histogram data points (plain table; BRIN on time + btree natural lookup)
CREATE TABLE histogram_data_points (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
CREATE INDEX idx_histogram_metric_time ON histogram_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_histogram_time_brin   ON histogram_data_points USING BRIN ("time_unix_nano");

-- Exponential histogram data points (plain table; BRIN on time + btree natural lookup)
CREATE TABLE exponential_histogram_data_points (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
CREATE INDEX idx_exp_histogram_metric_time ON exponential_histogram_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_exp_histogram_time_brin   ON exponential_histogram_data_points USING BRIN ("time_unix_nano");

-- Summary data points (plain table; BRIN on time + btree natural lookup)
CREATE TABLE summary_data_points (
    "id"                BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
CREATE INDEX idx_summary_metric_time ON summary_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_summary_time_brin   ON summary_data_points USING BRIN ("time_unix_nano");

-- Exemplars (for metrics; referenced by FK from data point tables)
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

-- Log records (plain table; BRIN on time + btree natural lookup).
-- TimeUnixNano is NOT NULL with DEFAULT 0 to handle edge-case OTLP records.
CREATE TABLE log_records (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
CREATE INDEX idx_log_time          ON log_records ("time_unix_nano"         DESC);
CREATE INDEX idx_log_time_brin     ON log_records USING BRIN ("time_unix_nano");
CREATE INDEX idx_observed_time     ON log_records ("observed_time_unix_nano" DESC);
CREATE INDEX idx_severity          ON log_records ("severity_number");
CREATE INDEX idx_log_severity_time ON log_records ("severity_number", "time_unix_nano" DESC);
CREATE INDEX idx_log_trace_span    ON log_records ("trace_id", "span_id");
CREATE INDEX idx_log_resource_time ON log_records ("resource_id", "time_unix_nano" DESC);
CREATE INDEX idx_log_attributes_gin ON log_records USING GIN ("attributes_json");

-- =============================================================================
-- UTILITY TABLES
-- =============================================================================

CREATE TABLE schema_version (
    "version"   VARCHAR(20) PRIMARY KEY,
    "applied_at" TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
-- NOTE: the schema_version row is seeded at the very END of this script (after all
-- tables and views), so a partial/failed apply never records a version that the
-- apply-schema.sh version gate would wrongly treat as "already applied".

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

-- Log severity distribution by day.
-- The TimescaleDB schema uses a continuous aggregate (log_severity_stats_daily) and
-- exposes log_severity_stats as a compatibility alias over it. Plain PostgreSQL has no
-- continuous aggregate, so log_severity_stats is computed on demand here. Same column
-- shape (severity_text, severity_number, count, log_date) so read repos don't branch.
-- The day bucket is computed by truncating nanoseconds-since-epoch to a DATE.
CREATE VIEW log_severity_stats AS
SELECT
    "severity_text",
    "severity_number",
    COUNT(*)                                              AS "count",
    CAST(to_timestamp("time_unix_nano" / 1000000000.0) AS DATE) AS "log_date"
FROM log_records
WHERE "time_unix_nano" > 0
GROUP BY
    "severity_text",
    "severity_number",
    CAST(to_timestamp("time_unix_nano" / 1000000000.0) AS DATE);

-- =============================================================================
-- SCHEMA VERSION (recorded LAST)
-- =============================================================================
-- Only reached when every statement above succeeded, so a partial apply cannot
-- leave a false version marker for the apply-schema.sh gate.
INSERT INTO schema_version ("version") VALUES ('2.5.0')
ON CONFLICT ("version") DO UPDATE
SET "applied_at" = NOW();

-- =============================================================================
-- NOTES
-- =============================================================================
--
-- Differences from Timescale-Schema.sql (same logical table/column set):
--
-- 1.  No CREATE EXTENSION timescaledb.
-- 2.  Metric data-point tables and log_records are plain heap tables with a
--     PRIMARY KEY on "id" (no create_hypertable, no partition column constraint).
-- 3.  No set_integer_now_func / telemetry_now_ns: lifecycle is not policy-driven.
-- 4.  No compression or retention policies. Manage data lifecycle externally
--     (e.g. scheduled DELETE jobs) if needed.
-- 5.  log_severity_stats is a plain on-demand view rather than a compatibility
--     alias over a continuous aggregate. Column shape is identical.
-- 6.  Time-series access uses a BRIN index on "time_unix_nano" (cheap, append-
--     friendly, what hypertable chunk exclusion approximated) plus the existing
--     (metric_id|resource_id|severity, time) btree indexes for point lookups.
--
-- jsonb columns, ON CONFLICT upserts, and all UNIQUE constraints (resource_hash,
-- scope_hash, api_keys.key_hash, uk_trace_span) are preserved exactly.
--
-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) List all user tables created
--    SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;
--
-- 2) Confirm no hypertables exist (timescaledb not required/installed)
--    -- This schema intentionally creates plain tables only.
--
-- 3) Schema version
--    SELECT * FROM schema_version;
